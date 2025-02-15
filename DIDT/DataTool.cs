﻿using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DIDT
{
    public static class DataTool
    {
        public static string updateDomainURL = @"https://g67ena.update.easebar.com/";
        public static string updateDomainURLCN = @"https://g67.update.netease.com/";
        public static string gphDomainURL = @"https://g67ena.gph.easebar.com/";
        public static string gphDomainURLCN = @"https://g67.gph.netease.com/";
        static string medium = "medium";
        static string cacheDir = AppContext.BaseDirectory + @"\Cache\";

        public static string osTypeString;
        public static string gameVersionString;

        //public static string patchListUrl;
        //public static string patchListFileName;
        //public static PatchList patchList;

        //public static string indexURL;
        //public static string indexFileName;
        //public static string indexFilePath;
        public static Dictionary<string, string> downloadUUIDs;
        public static Dictionary<string, Index> indices;
        public static bool running;
        static bool endSession = false;
        static Thread workerThread;

        public enum OSType { IOS = 0, Android = 1 };

        public enum GameVersion { PublicAlpha1, PublicAlpha2, PublicAlpha2CN, Beta };

        public static Dictionary<GameVersion, string> gameVersionLinkStrings = new Dictionary<GameVersion, string>()
        { 
            { GameVersion.PublicAlpha1, "PubAlpha1ROW" },
            { GameVersion.PublicAlpha2, "PubAlpha2ROW" },
            { GameVersion.PublicAlpha2CN, "PubAlpha2CNCN" },
            { GameVersion.Beta, "BetaROW" },
        };

        private static IProgress<int> _progress;

        /// <summary>
        /// Run on startup
        /// </summary>
        public static void Initialize(IProgress<int> progress)
        {
            _progress = progress;
        }

        /// <summary>
        /// Begin a new session
        /// </summary>
        public static void BeginSession()
        {
            // https://mbdl.update.easebar.com/g67ena.mbdl -- gives base64 encoded text that contains domain names -- https://www.base64decode.org/

            DownloadPatchList();
        }

        public static void EndSession()
        {
            endSession = true;
            Debug.Log("Ending Session ...");
        }

        static void DownloadPatchList()
        {
            string patchListUrl = Program.window.GetPatchListPath();
            osTypeString = Program.window.GetOSTypeString();
            gameVersionString = Program.window.GetGameVersionString();
            string patchListFileName = Path.GetFileNameWithoutExtension(patchListUrl);
            indices = new Dictionary<string, Index>();
            downloadUUIDs = new Dictionary<string, string>();
            string gphUrl = gameVersionString.Contains("CN") ? gphDomainURLCN : gphDomainURL;

            workerThread = new Thread(() =>
            {
                if (!Directory.Exists(cacheDir))
                    Directory.CreateDirectory(cacheDir);

                try
                {
                    using (WebClient wc = new WebClient())
                    {
                        wc.DownloadFile(
                            // Param1 = Link of file
                            new Uri(patchListUrl),
                            // Param2 = Path to save
                            cacheDir + patchListFileName
                        );

                        wc.DownloadProgressChanged += (s, e) =>
                        {
                            _progress.Report(e.ProgressPercentage);
                        };
                    }

                    using (Stream str = System.IO.File.OpenRead(cacheDir + patchListFileName))
                    {
                        using (StreamReader reader = new StreamReader(str))
                        {
                            string jsonData = reader.ReadToEnd();
                            var patchList = JsonConvert.DeserializeObject<PatchList>(jsonData);

                            foreach (KeyValuePair<string, string> item in patchList.patch_timestamp)
                            {
                                if (endSession)
                                    return;

                                string type = item.Key;
                                string uuid = item.Value;

                                downloadUUIDs.Add(type, uuid);
                                Debug.Log("Found UUID : " + type + " " + uuid);

                                string indexURL = gphUrl + uuid + "/" + osTypeString.ToLower() + "_" + medium + "_" + "index";
                                DownloadIndex(indexURL, type);
                            }
                        }
                    }

                    if (endSession)
                        return;

                    DownloadRepository();
                }
                catch (Exception e)
                {
                    Debug.Log(e.Message);
                }

                if (endSession)
                {
                    Debug.Log("Ended.");
                    endSession = false;
                }
            });

            workerThread.Start();
        }

        static void DownloadIndex(string indexURL, string uuidType)
        {
            Debug.Log("Downloading Index : " + uuidType + " " + indexURL);
            string indexFileName = Path.GetFileNameWithoutExtension(indexURL);
            string indexFilePath = cacheDir + indexFileName + "_" + uuidType;
            try
            {
                using (WebClient wc = new WebClient())
                {
                    wc.DownloadFile(
                        // Param1 = Link of file
                        new Uri(indexURL),
                        // Param2 = Path to save
                        indexFilePath
                    );

                    wc.DownloadProgressChanged += (s, e) =>
                    {
                        _progress.Report(e.ProgressPercentage);
                    };
                }

                using (Stream str = System.IO.File.OpenRead(indexFilePath))
                {
                    if (str.Length > 0)
                    {
                        using (BinaryReader br = new BinaryReader(str, Encoding.ASCII))
                        {
                            indices.Add(uuidType, new Index(br));
                        }
                    }
                }
            }
            catch (Exception e)
            {
                Debug.Log(e.Message);
            }
        }

        static object downloadCounterLockObj = new object();

        static void DownloadRepository()
        {
            // Create Data Folder //
            string dataDir = AppContext.BaseDirectory + @"\Data_" + gameVersionString + "_" + osTypeString + @"\";
            if (!Directory.Exists(dataDir))
                Directory.CreateDirectory(dataDir);

            // Calculate total files so we can monitor progress //
            float totalFiles = 0;
            foreach (KeyValuePair<string, Index> index in indices)
            {
                for (int b = 0; b < index.Value.blockCount; b++)
                {
                    Index.Block block = index.Value.blocks[b];
                    totalFiles += block.fileCount;
                }
            }

            float fileCounter = 0;
            string gphUrl = gameVersionString.Contains("CN") ? gphDomainURLCN : gphDomainURL;
            foreach (KeyValuePair<string, Index> index in indices)
            {
                for (int b = 0; b < index.Value.blockCount; b++)
                {
                    Index.Block block = index.Value.blocks[b];
                    int blockID = block.ID;

                    // Download each file //
                    List<(int Index, byte[] Data)> blocksData = new List<(int, byte[])>();

                    Parallel.For(0, block.fileCount, new ParallelOptions { MaxDegreeOfParallelism = 6 }, (f) =>
                    {
                        if (endSession)
                            return;

                        string fileURL = gphUrl + downloadUUIDs[index.Key] + "/" + osTypeString.ToLower() + "_" + medium + "_" + blockID + "." + f;
                        string fileName = Path.GetFileName(fileURL);

                        Debug.Log("Downloading | " + fileURL);

                        using (WebClient client = new WebClient())
                        {
                            var data = client.DownloadData(fileURL);
                            blocksData.Add((f, data));

                            _progress.Report((int)(fileCounter / totalFiles * 100));
                            lock (downloadCounterLockObj)
                            {
                                fileCounter++;
                            }
                        }
                    });

                    string bufferFilePath = cacheDir + $"{downloadUUIDs[index.Key]}_{blockID}.temp";
                    using (var oStr = System.IO.File.Create(bufferFilePath))
                    {
                        var blocksSorted = blocksData.OrderBy(b => b.Index);

                        // Merge into single file
                        foreach (var blockData in blocksSorted)
                        {
                            oStr.Write(blockData.Data);
                        }

                        blocksSorted = null;
                        blocksData.Clear();
                        blocksData = null;
                        GC.Collect();

                        Debug.Log($"Extracting | {downloadUUIDs[index.Key]} Block {blockID}");

                        // Process buffer //
                        oStr.Position = 0;
                        using (BinaryReader br = new BinaryReader(oStr, Encoding.ASCII))
                        {
                            while (oStr.Position < oStr.Length)
                            {
                                int strSize = br.ReadInt32();
                                string UUID = new string(br.ReadChars(strSize));
                                int fileSize = br.ReadInt32();
                                char[] hash = br.ReadChars(32);

                                // Read whole file //
                                byte[] data = br.ReadBytes(fileSize);
                                string dir = dataDir + Path.GetDirectoryName(UUID);
                                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                                System.IO.File.WriteAllBytes(dataDir + UUID, data);
                                Console.WriteLine(UUID);
                            }
                        }
                    }

                    if (System.IO.File.Exists(bufferFilePath)) System.IO.File.Delete(bufferFilePath);
                }
            }
            Debug.Log("Complete");
            _progress.Report(0);
        }
    }
}
