using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Linq;
using System.Xml.Linq;
using System.Collections.Generic;

namespace DentalManagerPlugin
{
    /// <summary>
    /// for optimal extracting of parts of a DentalSystem order (not zipped to begin with!)
    /// </summary>
    public class OrderHandler
    {
        private readonly DirectoryInfo _orderDirectoryInfo;

        /// <summary>
        /// ID of order = name of its directory
        /// </summary>
        public string OrderId => _orderDirectoryInfo?.Name;

        private FileInfo OrderFileInfo => new FileInfo(Path.Combine(_orderDirectoryInfo.FullName, _orderDirectoryInfo.Name + ".xml"));

        private OrderHandler(DirectoryInfo validOrderDirInfo)
        {
            _orderDirectoryInfo = validOrderDirInfo;
        }

        /// <summary>
        /// create with order directory. Returns null if not valid or not containing any order
        /// </summary>
        /// <param name="orderDir">full path to directory</param>
        public static OrderHandler MakeIfValid(string orderDir)
        {
            if (!Directory.Exists(orderDir))
                return null;

            var odi = new DirectoryInfo(orderDir);
            var orderFile = Path.Combine(orderDir, odi.Name + ".xml");
            if (!File.Exists(orderFile))
                return null;

            return new OrderHandler(odi);
        }

        private string RelativePath(string fullPath)
        {
            var fn = _orderDirectoryInfo.Parent == null ? fullPath : _orderDirectoryInfo.Parent.FullName;
            fn = fn.TrimEnd('\\').TrimEnd('/');
            if (!fullPath.StartsWith(fn))
                return fullPath;

            var relPath = fullPath.Substring(fn.Length);
            if (relPath.StartsWith("\\") || relPath.StartsWith("/"))
                relPath = relPath.Substring(1);

            relPath = relPath.Replace("\\", "/"); // zip convention
            return relPath;
        }

        private string FullPath(string relPath) => Path.Combine(_orderDirectoryInfo.Parent == null ?
            _orderDirectoryInfo.FullName : _orderDirectoryInfo.Parent.FullName, relPath);


        /// <summary>
        /// all files within order directory. Paths start with OrderId/
        /// </summary>
        public IEnumerable<string> AllRelativePaths => _orderDirectoryInfo.EnumerateFiles("*", SearchOption.AllDirectories)
            .Select(fi => RelativePath(fi.FullName));

        /// <summary>
        /// return stream (FileStream) or null if file given by <paramref name="relPath"/> does not exist or if <paramref name="relPath"/>
        /// is empty or null
        /// </summary>
        public Stream GetStream(string relPath)
        {
            if (string.IsNullOrEmpty(relPath))
                return null;

            var pa = FullPath(relPath);
            if (!File.Exists(pa))
                return null;

            return new FileStream(pa, FileMode.Open);
        }

        public void GetStatusInfo(out DateTime creationDateUtc, out bool isScanned, out bool isLocked)
        {
            creationDateUtc = DateTime.MinValue;
            isScanned = false;
            isLocked = false;

            try
            {
                var doc = XDocument.Load(OrderFileInfo.FullName);

                // find the first type = TDM_Item_ModelElement node
                if (!(doc.Root?.Descendants().FirstOrDefault(n => n.Attribute("type")?.Value == "TDM_Item_ModelElement")
                    is XElement meItem))
                    return;

                if (meItem.Descendants().FirstOrDefault(n => n.Attribute("name")?.Value == "CreateDate") is XElement creaDate)
                {
                    var unixDate = creaDate.Attribute("value")?.Value;
                    if (unixDate != null && double.TryParse(unixDate, out var unixTimeStamp))
                    {
                        // Unix timestamp is seconds past epoch
                        var dtDateTime = new DateTime(1970, 1, 1, 0, 0, 0, 0, System.DateTimeKind.Utc);
                        creationDateUtc = dtDateTime.AddSeconds(unixTimeStamp);
                    }
                }

                if (meItem.Descendants().FirstOrDefault(n => n.Attribute("name")?.Value == "ProcessStatusID") is XElement psId)
                    isScanned = psId.Attribute("value")?.Value == "psScanned";

                if (meItem.Descendants().FirstOrDefault(n => n.Attribute("name")?.Value == "ProcessLockID") is XElement plId)
                    isLocked = plId.Attribute("value")?.Value == "plCheckedOut";
            }
            catch (Exception)
            { // nothing
            }
        }

        /// <summary>
        /// pack all relevant order files (as indivates by <paramref name="relPaths"/>) in a zip archive, as stream.
        /// Is set to Position 0. Caller must dispose. Does not add preferences. Back end will use user's.
        /// </summary>
        public MemoryStream ZipOrderFiles(IEnumerable<string> relPaths)
        {
            var res = new MemoryStream();
            using (var newArchive = new ZipArchive(res, ZipArchiveMode.Create, true, Encoding.UTF8))
            {
                foreach (var relPath in relPaths)
                {
                    // careful with sub-directories (scans)
                    var fullPath = FullPath(relPath);
                    var newEntry = newArchive.CreateEntry(relPath.Replace("\\", "/")); // to be safe
                    using (var fromStream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                    using (var toStream = newEntry.Open())
                        fromStream.CopyTo(toStream);
                }
            }

            res.Seek(0, SeekOrigin.Begin);
            return res;
        }
    }
}
