using AtariST.SerialDisk.Common;
using AtariST.SerialDisk.Interfaces;
using AtariST.SerialDisk.Models;
using AtariST.SerialDisk.Utilities;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace AtariST.SerialDisk.Storage
{
    public class Disk : IDisk
    {
        private readonly int _rootDirectoryClusterIndex = 0;
        private byte[] _rootDirectoryBuffer;
        private byte[] _fatBuffer;
        private int _previousFreeClusterIndex;

        private ClusterInfo[] _clusterInfos;
        private List<LocalDirectoryContentInfo> _localDirectoryContentInfos;
        private readonly ILogger _logger;

        public DiskParameters Parameters { get; set; }

        public Disk(DiskParameters diskParams, ILogger logger)
        {
            _logger = logger;
            Parameters = diskParams;

            try
            {
                int maxRootDirectoryEntries = ((diskParams.RootDirectorySectors * diskParams.BytesPerSector) / 32) - 2; // Each entry is 32 bytes, 2 entries reserved for . and ..
                FAT16Helper.ValidateLocalDirectory(diskParams.LocalDirectoryPath, diskParams.DiskTotalBytes, maxRootDirectoryEntries, diskParams.TOS);
            }

            catch (Exception ex)
            {
                _logger.LogException(ex, ex.Message);
                throw ex;
            }

            FatImportLocalDirectoryContents(Parameters.LocalDirectoryPath, _rootDirectoryClusterIndex);
        }

        private void SyncLocalDisk(int clusterIndex, bool syncSubDirectoryContents = true)
        {
            byte[] directoryData;

            directoryData = GetDirectoryClusterData(clusterIndex);

            // Only check for changes if this cluster contains directory entry information
            if (FAT16Helper.IsDirectoryCluster(directoryData, clusterIndex))
            {
                bool IsEndOfDirectoryEntries = false;

                while (!FAT16Helper.IsEndOfFile(clusterIndex) && !IsEndOfDirectoryEntries)
                {
                    _logger.Log($"Updating cluster {clusterIndex}", Constants.LoggingLevel.All);
                    int directoryEntryIndex = 0;

                    while (directoryEntryIndex < directoryData.Length && directoryData[directoryEntryIndex] != 0)
                    {
                        // The entry is not "." or "..".
                        if (directoryData[directoryEntryIndex] != 0x2e)
                        {
                            string fileName = ASCIIEncoding.ASCII.GetString(directoryData, directoryEntryIndex, 8).Trim();
                            string fileExtension = ASCIIEncoding.ASCII.GetString(directoryData, directoryEntryIndex + 8, 3).Trim();

                            if (fileExtension != "")
                                fileName += "." + fileExtension;

                            int entryStartClusterIndex = directoryData[directoryEntryIndex + 26] | (directoryData[directoryEntryIndex + 27] << 8);

                            // Find the matching local content and check what happened to it.
                            string LocalDirectoryContentPath = "";

                            for (int contentIndex = 0; contentIndex < _localDirectoryContentInfos.Count; contentIndex++)
                            {
                                if (_localDirectoryContentInfos[contentIndex].EntryIndex == directoryEntryIndex
                                    && _localDirectoryContentInfos[contentIndex].DirectoryCluster == clusterIndex)
                                {
                                    var directoryContentInfo = _localDirectoryContentInfos[contentIndex];

                                    LocalDirectoryContentPath = directoryContentInfo.LocalPath;

                                    if (directoryContentInfo.ShortFileName != fileName)
                                    {
                                        if (directoryData[directoryEntryIndex] == 0xe5) // Has the entry been deleted?
                                        {
                                            if (directoryData[directoryEntryIndex + 11] == 0x10) // Is it a directory?
                                            {
                                                _logger.Log($"Deleting local directory \"{ directoryContentInfo.LocalPath}\".", Constants.LoggingLevel.Info);

                                                Directory.Delete(directoryContentInfo.LocalPath, true);
                                            }

                                            else // It's a file
                                            {
                                                _logger.Log($"Deleting local file \"{directoryContentInfo.LocalPath}\".", Constants.LoggingLevel.Info);

                                                File.Delete(directoryContentInfo.LocalPath);
                                            }

                                            _localDirectoryContentInfos.Remove(directoryContentInfo);

                                            _clusterInfos
                                                .Where(ci => ci?.LocalPath == directoryContentInfo.LocalPath)
                                                .ToList()
                                                .ForEach(ci => ci.LocalDirectory = null);
                                        }

                                        // Entry has been renamed.
                                        else
                                        {
                                            string oldContentPath = directoryContentInfo.LocalPath;
                                            string newContentPath = Path.Combine(_clusterInfos[clusterIndex].LocalPath, fileName);

                                            // Is it a directory?
                                            if (directoryData[directoryEntryIndex + 11] == 0x10)
                                            {
                                                _logger.Log($"Renaming local directory \"{oldContentPath}\" to \"{newContentPath}\".",
                                                    Constants.LoggingLevel.Info);

                                                _clusterInfos
                                                    .Where(ci => ci != null && ci.LocalDirectory == oldContentPath)
                                                    .ToList()
                                                    .ForEach(ci => ci.LocalDirectory = newContentPath);

                                                _localDirectoryContentInfos
                                                    .Where(ldci => ldci != null && ldci.LocalDirectory == oldContentPath)
                                                    .ToList()
                                                    .ForEach(ldci => ldci.LocalDirectory = newContentPath);

                                                Directory.Move(oldContentPath, newContentPath);
                                            }

                                            // It's a file
                                            else
                                            {
                                                _logger.Log($"Renaming local file \"{oldContentPath}\" to \"{newContentPath}\".",
                                                    Constants.LoggingLevel.Info);

                                                _clusterInfos
                                                    .Where(ci => ci != null && ci.LocalPath == oldContentPath)
                                                    .ToList()
                                                    .ForEach(ci => ci.LocalFileName = newContentPath);

                                                directoryContentInfo.LocalDirectory = _clusterInfos[clusterIndex].LocalDirectory;
                                                directoryContentInfo.LocalFileName = fileName;
                                                directoryContentInfo.ShortFileName = fileName;

                                                File.Move(directoryContentInfo.LocalPath, newContentPath);
                                            }
                                        }
                                    }

                                    break;
                                }
                            }

                            // Is the content new but not been deleted
                            if (String.IsNullOrEmpty(LocalDirectoryContentPath) && directoryData[directoryEntryIndex] != 0xe5)
                            {
                                string newPathDirectory = "";
                                if (clusterIndex != _rootDirectoryClusterIndex) newPathDirectory = _clusterInfos[clusterIndex].LocalPath; // Subdirectory
                                else newPathDirectory = Parameters.LocalDirectoryPath; // Root dir

                                try
                                {
                                    // Is it a directory with a valid start cluster?
                                    if (directoryData[directoryEntryIndex + 11] == 0x10)
                                    {
                                        if (entryStartClusterIndex != 0)
                                        {
                                            _logger.Log("Creating local directory \"" + newPathDirectory + "\".", Constants.LoggingLevel.Info);

                                            var CreatedLocalDirectory = Directory.CreateDirectory(newPathDirectory);

                                            _clusterInfos[entryStartClusterIndex].FileOffset = -1;
                                            _clusterInfos[entryStartClusterIndex].LocalDirectory = newPathDirectory;

                                            _localDirectoryContentInfos.Add(new LocalDirectoryContentInfo
                                            {
                                                LocalDirectory = newPathDirectory,
                                                ShortFileName = fileName,
                                                EntryIndex = directoryEntryIndex,
                                                DirectoryCluster = clusterIndex,
                                                StartCluster = entryStartClusterIndex,

                                            });
                                        }
                                    }

                                    // it's a file
                                    else
                                    {
                                        int fileClusterIndex = entryStartClusterIndex;

                                        int fileSize = directoryData[directoryEntryIndex + 28] | (directoryData[directoryEntryIndex + 29] << 8) | (directoryData[directoryEntryIndex + 30] << 16) | (directoryData[directoryEntryIndex + 31] << 24);

                                        if (fileSize == 0 && !File.Exists(newPathDirectory))
                                        {
                                            _logger.Log($"Creating local file: {newPathDirectory}", Constants.LoggingLevel.Info);
                                            File.Create(newPathDirectory).Dispose();
                                        }

                                        else if (entryStartClusterIndex != 0)
                                        {
                                            // Check if the file has been completely written.
                                            while (!FAT16Helper.IsEndOfClusterChain(fileClusterIndex))
                                            {
                                                fileClusterIndex = FatGetClusterValue(fileClusterIndex);
                                            }

                                            if (FAT16Helper.IsEndOfFile(fileClusterIndex))
                                            {
                                                try
                                                {
                                                    _logger.Log("Writing local file \"" + newPathDirectory + "\".", Constants.LoggingLevel.Info);

                                                    using (BinaryWriter FileBinaryWriter = new BinaryWriter(File.OpenWrite(newPathDirectory)))
                                                    {
                                                        fileClusterIndex = entryStartClusterIndex;

                                                        while (!FAT16Helper.IsEndOfFile(fileClusterIndex))
                                                        {
                                                            _clusterInfos[fileClusterIndex].LocalDirectory = newPathDirectory;
                                                            _clusterInfos[fileClusterIndex].LocalFileName = fileName;

                                                            FileBinaryWriter.Write(_clusterInfos[fileClusterIndex].DataBuffer, 0, Math.Min(_clusterInfos[fileClusterIndex].DataBuffer.Length, fileSize));

                                                            fileSize -= _clusterInfos[fileClusterIndex].DataBuffer.Length;

                                                            fileClusterIndex = FatGetClusterValue(fileClusterIndex);
                                                        }
                                                    }

                                                    _localDirectoryContentInfos.Add(new LocalDirectoryContentInfo
                                                    {
                                                        LocalDirectory = newPathDirectory,
                                                        LocalFileName = fileName,
                                                        ShortFileName = fileName,
                                                        EntryIndex = directoryEntryIndex,
                                                        DirectoryCluster = clusterIndex,
                                                        StartCluster = entryStartClusterIndex,

                                                    }); ;
                                                }

                                                catch (Exception ex)
                                                {
                                                    _logger.LogException(ex);
                                                }
                                            }
                                        }
                                    }
                                }

                                catch (Exception ex)
                                {
                                    _logger.LogException(ex);
                                }

                            }

                            // Recurse non-deleted directories
                            if (syncSubDirectoryContents
                                && directoryData[directoryEntryIndex + 11] == 0x10
                                && directoryData[directoryEntryIndex] != 0xe5)
                            {
                                SyncLocalDisk(entryStartClusterIndex);
                            }
                        }

                        directoryEntryIndex += 32;
                    }

                    if (directoryEntryIndex < directoryData.Length)
                    {
                        IsEndOfDirectoryEntries = true;
                    }

                    else
                    {
                        clusterIndex = FatGetClusterValue(clusterIndex);
                        directoryData = GetDirectoryClusterData(clusterIndex);
                    }
                }
            }
        }

        private byte[] GetDirectoryClusterData(int directoryClusterIndex)
        {
            byte[] directoryClusterData;

            if (directoryClusterIndex == 0)
                directoryClusterData = _rootDirectoryBuffer;
            else
                directoryClusterData = _clusterInfos[directoryClusterIndex].DataBuffer;

            return directoryClusterData;
        }

        private int FatGetClusterValue(int clusterIndex, int directoryClusterIndex = 0)
        {
            int cluster = clusterIndex * 2;
            if (directoryClusterIndex != 0) cluster -= Parameters.RootDirectorySectors;
            return _fatBuffer[cluster + 1] << 8 | _fatBuffer[cluster];
        }

        private int GetNextFreeClusterIndexAndAssignToCluster(int currentCluster)
        {
            int newClusterIndex = GetNextFreeClusterIndex();

            _fatBuffer[currentCluster * 2] = (byte)(newClusterIndex & 0xff);
            _fatBuffer[currentCluster * 2 + 1] = (byte)((newClusterIndex >> 8) & 0xff);

            _fatBuffer[newClusterIndex * 2] = 0xff;
            _fatBuffer[newClusterIndex * 2 + 1] = 0xff;

            return newClusterIndex;
        }

        private int GetNextFreeClusterIndex()
        {
            int maxClusterIndex = _fatBuffer.Length / 2;
            int newClusterIndex = _previousFreeClusterIndex; // Start check at previous index for performance
            int newClusterValue = 0xFFFF;

            try
            {
                while (newClusterValue != 0)
                {
                    newClusterIndex++;
                    if (newClusterIndex > maxClusterIndex) newClusterIndex = 2; // End of buffer reached, loop back to beginning
                    else if (newClusterIndex == _previousFreeClusterIndex) throw new Exception("Could not find a free cluster in FAT"); // We have looped without finding a free cluster
                    newClusterValue = FatGetClusterValue(newClusterIndex);
                }

                _previousFreeClusterIndex = newClusterIndex;
            }

            catch (Exception ex)
            {
                _logger.LogException(ex);
                newClusterIndex = -1;
            }

            return newClusterIndex;
        }

        public byte[] ReadSectors(int sector, int numberOfSectors)
        {
            int firstSector = sector;
            byte[] dataBuffer = new byte[numberOfSectors * Parameters.BytesPerSector];
            int dataOffset = 0;

            while (numberOfSectors > 0)
            {
                // FAT area
                if (sector < Parameters.SectorsPerFat * 2)
                {
                    int readSector = sector;

                    if (readSector >= Parameters.SectorsPerFat)
                        readSector -= Parameters.SectorsPerFat;

                    Array.Copy(_fatBuffer, readSector * Parameters.BytesPerSector, dataBuffer, dataOffset, Parameters.BytesPerSector);
                }

                // Root directory
                else if (sector < Parameters.SectorsPerFat * 2 + Parameters.RootDirectorySectors)
                {
                    Array.Copy(_rootDirectoryBuffer, (sector - Parameters.SectorsPerFat * 2) * Parameters.BytesPerSector, dataBuffer, dataOffset, Parameters.BytesPerSector);
                }

                // DATA area
                else
                {
                    int readSector = sector - (Parameters.SectorsPerFat * 2 + Parameters.RootDirectorySectors) + 2 * Parameters.SectorsPerCluster;
                    int clusterIndex = readSector / Parameters.SectorsPerCluster;

                    if (_clusterInfos[clusterIndex] != null)
                    {
                        if (_clusterInfos[clusterIndex].DataBuffer != null)
                        {
                            Array.Copy(_clusterInfos[clusterIndex].DataBuffer, (readSector - clusterIndex * Parameters.SectorsPerCluster) * Parameters.BytesPerSector, dataBuffer, dataOffset, Parameters.BytesPerSector);
                        }
                        else
                        {
                            if (_clusterInfos[clusterIndex].LocalPath != null)
                            {
                                string contentName = _clusterInfos[clusterIndex].LocalPath;

                                if (firstSector == sector) _logger.Log($"Reading local file {contentName}", Constants.LoggingLevel.Info);

                                byte[] fileClusterDataBuffer = new byte[Parameters.BytesPerCluster];

                                try
                                {
                                    using FileStream fileStream = File.OpenRead(contentName);

                                    int bytesToRead = Math.Min(Parameters.BytesPerCluster, (int)(fileStream.Length - _clusterInfos[clusterIndex].FileOffset));

                                    fileStream.Seek(_clusterInfos[clusterIndex].FileOffset, SeekOrigin.Begin);

                                    for (int Index = 0; Index < bytesToRead; Index++)
                                        fileClusterDataBuffer[Index] = (byte)fileStream.ReadByte();

                                    Array.Copy(fileClusterDataBuffer, (readSector - clusterIndex * Parameters.SectorsPerCluster) * Parameters.BytesPerSector, dataBuffer, dataOffset, Parameters.BytesPerSector);
                                }

                                catch (Exception ex)
                                {
                                    _logger.LogException(ex, "Error reading sectors");
                                }
                            }
                        }
                    }
                }

                dataOffset += Parameters.BytesPerSector;
                numberOfSectors--;
                sector++;
            }

            return dataBuffer;
        }

        public void WriteSectors(int receiveBufferLength, int startSector, byte[] dataBuffer)
        {
            int sector = startSector;
            int numberOfSectors = (int)Math.Ceiling((decimal)receiveBufferLength / Parameters.BytesPerSector);
            int dataOffset = 0;
            int clusterIndex = 0;

            while (numberOfSectors > 0)
            {
                // FAT area?
                if (sector < Parameters.SectorsPerFat * 2)
                {
                    int WriteSector = sector;

                    // Force all writes to the first FAT
                    if (WriteSector >= Parameters.SectorsPerFat)
                        WriteSector -= Parameters.SectorsPerFat;

                    _logger.Log($"Updating FAT sector {WriteSector}", Constants.LoggingLevel.All);

                    Array.Copy(dataBuffer, dataOffset, _fatBuffer, WriteSector * Parameters.BytesPerSector, Parameters.BytesPerSector);

                    SyncLocalDisk(clusterIndex, true);
                }

                // Root directory area?
                else if (sector < Parameters.SectorsPerFat * 2 + Parameters.RootDirectorySectors)
                {
                    _logger.Log($"Updating ROOT directory sector {sector}", Constants.LoggingLevel.All);

                    Array.Copy(dataBuffer, dataOffset, _rootDirectoryBuffer, (sector - Parameters.SectorsPerFat * 2) * Parameters.BytesPerSector, Parameters.BytesPerSector);

                    // Root directory must be synced independently
                    SyncLocalDisk(_rootDirectoryClusterIndex, false); 
                }

                // Data area, used for files and non-root directories
                else
                {
                    int WriteSector = sector - (Parameters.SectorsPerFat * 2 + Parameters.RootDirectorySectors) + 2 * Parameters.SectorsPerCluster;

                    clusterIndex = WriteSector / Parameters.SectorsPerCluster;

                    _logger.Log($"Updating DATA sector {WriteSector}, cluster {clusterIndex}", Constants.LoggingLevel.All);

                    if (_clusterInfos[clusterIndex] == null) _clusterInfos[clusterIndex] = new ClusterInfo();
                    if (_clusterInfos[clusterIndex].DataBuffer == null) _clusterInfos[clusterIndex].DataBuffer = new byte[Parameters.BytesPerCluster];
                    if (String.IsNullOrEmpty(_clusterInfos[clusterIndex].LocalPath))
                    {
                        // Get content name by walking backwards through the FAT cluster values
                        var contentInfo = _localDirectoryContentInfos.Where(dci => FatGetClusterValue(dci.StartCluster) == clusterIndex).FirstOrDefault();
                        if (contentInfo != null)
                        {
                            _clusterInfos[clusterIndex].LocalDirectory = contentInfo.LocalDirectory;
                            _clusterInfos[clusterIndex].LocalFileName = contentInfo.LocalFileName;
                        }
                    }

                    Array.Copy(dataBuffer, dataOffset, _clusterInfos[clusterIndex].DataBuffer, (WriteSector - clusterIndex * Parameters.SectorsPerCluster) * Parameters.BytesPerSector, Parameters.BytesPerSector);

                    // Empty files are not written to the FAT so must be synced via their containing directory
                    SyncLocalDisk(clusterIndex, false);
                }

                dataOffset += Parameters.BytesPerSector;
                numberOfSectors--;
                sector++;
            }
        }

        private bool FatAddDirectoryEntry(int directoryClusterIndex, string directoryPath, string fileName, string shortFileName, byte attributeFlags, DateTime lastWriteDateTime, long fileSize, int entryStartClusterIndex)
        {
            byte[] directoryBuffer;
            int entryIndex = 0;

            int maxEntryIndex = directoryClusterIndex == 0 ? _rootDirectoryBuffer.Length : Parameters.BytesPerCluster;

            if (directoryClusterIndex == _rootDirectoryClusterIndex)
                directoryBuffer = _rootDirectoryBuffer;
            else
                directoryBuffer = _clusterInfos[directoryClusterIndex].DataBuffer;

            // Check whether there is any space left in the cluster
            do
            {
                // No space left
                if (entryIndex >= maxEntryIndex)
                {
                    int nextDirectoryClusterIndex = FatGetClusterValue(directoryClusterIndex);

                    // This is the final cluster, allocate new cluster
                    if (FAT16Helper.IsEndOfFile(nextDirectoryClusterIndex))
                    {
                        try
                        {
                            int newDirectoryCluster = GetNextFreeClusterIndexAndAssignToCluster(directoryClusterIndex);

                            _clusterInfos[newDirectoryCluster] = new ClusterInfo()
                            {
                                LocalDirectory = _clusterInfos[directoryClusterIndex].LocalDirectory,
                                LocalFileName = _clusterInfos[directoryClusterIndex].LocalFileName,
                                FileOffset = -1,
                                DataBuffer = new byte[Parameters.BytesPerCluster]
                            };

                            entryIndex = 0;
                        }

                        catch (IndexOutOfRangeException outOfRangeEx)
                        {
                            int localDirectorySizeMiB = (int)Directory.GetFiles(Parameters.LocalDirectoryPath, "*", SearchOption.AllDirectories).Sum(file => (new FileInfo(file).Length)) / FAT16Helper.BytesPerMiB;
                            _logger.LogException(outOfRangeEx, $"Local directory size is {localDirectorySizeMiB} MiB, which is too large for the given virtual disk size ({Parameters.DiskTotalBytes / FAT16Helper.BytesPerMiB} MiB)");
                            throw outOfRangeEx;
                        }
                    }

                    else
                    {
                        directoryClusterIndex = nextDirectoryClusterIndex;
                    }

                    directoryBuffer = _clusterInfos[directoryClusterIndex].DataBuffer;
                    entryIndex = 0;
                }

                // Find next unused entry in directory
                while (entryIndex < maxEntryIndex && directoryBuffer[entryIndex] != 0)
                    entryIndex += 32;

                if (entryIndex >= maxEntryIndex)
                {
                    if (directoryClusterIndex == _rootDirectoryClusterIndex)
                    {
                        Exception outofIndexesException = new Exception($"Exceeded available directory entries in {_clusterInfos[directoryClusterIndex].LocalPath}. There may be too many files in directory (max {(maxEntryIndex / 32) - 2} items).");
                        _logger.LogException(outofIndexesException, outofIndexesException.Message);
                        throw outofIndexesException;
                    }
                }

            } while (entryIndex >= maxEntryIndex);

            // Remember which local content matches this entry.

            if (shortFileName != "." && shortFileName != "..")
            {
                LocalDirectoryContentInfo newLocalDirectoryContentInfo = new LocalDirectoryContentInfo()
                {
                    LocalDirectory = directoryPath,
                    LocalFileName = fileName,
                    ShortFileName = shortFileName,
                    EntryIndex = entryIndex,
                    DirectoryCluster = directoryClusterIndex,
                    StartCluster = entryStartClusterIndex
                };

                _localDirectoryContentInfos.Add(newLocalDirectoryContentInfo);
            }

            // File name.
            int fileNameIndex;

            for (fileNameIndex = 0; fileNameIndex < (8 + 3); fileNameIndex++)
                directoryBuffer[entryIndex + fileNameIndex] = 0x20;

            string[] nameAndExtender;
            byte[] asciiName;
            byte[] asciiExtender;

            if (shortFileName == "." || shortFileName == "..")
            {
                asciiName = ASCIIEncoding.ASCII.GetBytes(shortFileName);
                asciiExtender = null;
            }
            else
            {
                nameAndExtender = shortFileName.Split('.');
                asciiName = ASCIIEncoding.ASCII.GetBytes(nameAndExtender[0]);
                asciiExtender = nameAndExtender.Length == 2 ? ASCIIEncoding.ASCII.GetBytes(nameAndExtender[1]) : null;
            }

            for (fileNameIndex = 0; fileNameIndex < asciiName.Length; fileNameIndex++)
                directoryBuffer[entryIndex + fileNameIndex] = asciiName[fileNameIndex];

            if (asciiExtender != null)
                for (fileNameIndex = 0; fileNameIndex < asciiExtender.Length; fileNameIndex++)
                    directoryBuffer[entryIndex + 8 + fileNameIndex] = asciiExtender[fileNameIndex];

            // File attribute flags.

            directoryBuffer[entryIndex + 11] = attributeFlags;

            // File write time and date (little endian).

            UInt16 fatFileWriteTime = 0;
            UInt16 fatFileWriteDate = 0;

            int TwoSeconds = lastWriteDateTime.Second / 2;
            int Minutes = lastWriteDateTime.Minute;
            int Hours = lastWriteDateTime.Hour;
            int DayOfMonth = lastWriteDateTime.Day;
            int Month = lastWriteDateTime.Month;
            int YearsSince1980 = lastWriteDateTime.Year - 1980;

            fatFileWriteTime |= (UInt16)TwoSeconds;
            fatFileWriteTime |= (UInt16)(Minutes << 5);
            fatFileWriteTime |= (UInt16)(Hours << 11);

            fatFileWriteDate |= (UInt16)DayOfMonth;
            fatFileWriteDate |= (UInt16)(Month << 5);
            fatFileWriteDate |= (UInt16)(YearsSince1980 << 9);

            directoryBuffer[entryIndex + 22] = (byte)(fatFileWriteTime & 0xff);
            directoryBuffer[entryIndex + 23] = (byte)((fatFileWriteTime >> 8) & 0xff);
            directoryBuffer[entryIndex + 24] = (byte)(fatFileWriteDate & 0xff);
            directoryBuffer[entryIndex + 25] = (byte)((fatFileWriteDate >> 8) & 0xff);

            // Cluster (little endian).

            directoryBuffer[entryIndex + 26] = (byte)(entryStartClusterIndex & 0xff);
            directoryBuffer[entryIndex + 27] = (byte)((entryStartClusterIndex >> 8) & 0xff);

            // File size (little endian).

            directoryBuffer[entryIndex + 28] = (byte)(fileSize & 0xff);
            directoryBuffer[entryIndex + 29] = (byte)((fileSize >> 8) & 0xff);
            directoryBuffer[entryIndex + 30] = (byte)((fileSize >> 16) & 0xff);
            directoryBuffer[entryIndex + 31] = (byte)((fileSize >> 24) & 0xff);

            return true;
        }

        private void FatAddDirectory(DirectoryInfo directoryInfo, int directoryCluster)
        {
            int newDirectoryClusterIndex = GetNextFreeClusterIndexAndAssignToCluster(0); // Is there is a cleaner way to do this?

            _clusterInfos[newDirectoryClusterIndex] = new ClusterInfo()
            {
                LocalDirectory = directoryInfo.FullName,
                FileOffset = -1,
                DataBuffer = new byte[Parameters.BytesPerCluster]
            };

            FatAddDirectoryEntry(directoryCluster, directoryInfo.FullName, String.Empty, FAT16Helper.GetShortFileName(directoryInfo.Name), 0x10, directoryInfo.LastWriteTime, 0, newDirectoryClusterIndex);
            FatAddDirectoryEntry(newDirectoryClusterIndex, String.Empty, ".",".", 0x10, directoryInfo.LastWriteTime, 0, newDirectoryClusterIndex);
            FatAddDirectoryEntry(newDirectoryClusterIndex, String.Empty, "..","..", 0x10, directoryInfo.LastWriteTime, 0, directoryCluster);

            FatImportLocalDirectoryContents(directoryInfo.FullName, newDirectoryClusterIndex);
        }

        private void FatAddFile(FileInfo fileInfo, int directoryClusterIndex)
        {
            long fileOffset = 0;
            int fileentryStartClusterIndex = 0;
            int nextFileClusterIndex = 0;

            while (fileOffset < fileInfo.Length)
            {
                try
                {
                    nextFileClusterIndex = GetNextFreeClusterIndexAndAssignToCluster(nextFileClusterIndex);

                    if (fileentryStartClusterIndex == _rootDirectoryClusterIndex)
                        fileentryStartClusterIndex = nextFileClusterIndex;

                    _clusterInfos[nextFileClusterIndex] = new ClusterInfo()
                    {
                        LocalDirectory = fileInfo.DirectoryName,
                        LocalFileName = fileInfo.Name,
                        FileOffset = fileOffset
                    };

                    fileOffset += Parameters.BytesPerCluster;
                }

                catch (IndexOutOfRangeException outOfRangeEx)
                {
                    int localDirectorySizeMiB = (int)Directory.GetFiles(Parameters.LocalDirectoryPath, "*", SearchOption.AllDirectories).Sum(file => (new FileInfo(file).Length)) / FAT16Helper.BytesPerMiB;
                    _logger.LogException(outOfRangeEx, $"Local directory size is {localDirectorySizeMiB} MiB, which is too large for the given virtual disk size ({Parameters.DiskTotalBytes / FAT16Helper.BytesPerMiB} MiB)");
                    throw outOfRangeEx;
                }
            }

            // handle duplicate short filenames
            string shortFileName = FAT16Helper.GetShortFileName(fileInfo.Name);
            int duplicateId = 1;

            while (_localDirectoryContentInfos.Where(ldi => ldi.ShortFileName.Equals(shortFileName, StringComparison.InvariantCultureIgnoreCase) &&
                 ldi.DirectoryCluster == directoryClusterIndex).Any())
            {
                int numberStringLength = duplicateId.ToString().Length + 1; // +1 for ~
                int replaceIndex = shortFileName.LastIndexOf('.') != -1 ? shortFileName.LastIndexOf('.') : shortFileName.Length;
                replaceIndex -= numberStringLength;
                shortFileName = shortFileName.Remove(replaceIndex, numberStringLength).Insert(replaceIndex, $"~{duplicateId}");
                duplicateId++;
            }

            FatAddDirectoryEntry(directoryClusterIndex, fileInfo.DirectoryName, fileInfo.Name, shortFileName, 0x00, fileInfo.LastWriteTime, fileInfo.Length, fileentryStartClusterIndex);
        }

        public void FatImportLocalDirectoryContents(string directoryName, int directoryClusterIndex)
        {
            _previousFreeClusterIndex = 1;

            if (directoryClusterIndex == _rootDirectoryClusterIndex)
            {
                _rootDirectoryBuffer = new byte[Parameters.RootDirectorySectors * Parameters.BytesPerSector];
                _fatBuffer = new byte[Parameters.SectorsPerFat * Parameters.BytesPerSector];
                _clusterInfos = new ClusterInfo[Parameters.DiskClusters];
                _localDirectoryContentInfos = new List<LocalDirectoryContentInfo>();
            }

            DirectoryInfo directoryInfo = new DirectoryInfo(directoryName);

            foreach (DirectoryInfo subDirectoryInfo in directoryInfo.EnumerateDirectories())
                FatAddDirectory(subDirectoryInfo, directoryClusterIndex);

            foreach (FileInfo fileInfo in directoryInfo.EnumerateFiles())
                FatAddFile(fileInfo, directoryClusterIndex);
        }
    }
}
