using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Xml.Linq;

namespace DentalManagerPlugin
{
    /// <summary>
    /// for optimal extracting of parts of a DentalSystem order (not zipped to begin with!)
    /// </summary>
    public class OrderHandler
    {
        /// <summary>
        /// ID of order = name of its directory
        /// </summary>
        public string OrderId => _orderDirectoryInfo?.Name;

        private readonly DirectoryInfo _orderDirectoryInfo;
        private readonly FileInfo _orderFileInfo;
        private string _orderText;

        private OrderHandler()
        {
            // not allowed
        }

        private OrderHandler(DirectoryInfo di, FileInfo fi)
        {
            _orderDirectoryInfo = di;
            _orderFileInfo = fi;
        }

        public static OrderHandler MakeIfValid(DirectoryInfo orderDirInfo)
        {
            if (orderDirInfo == null || !orderDirInfo.Exists)
                return null;

            var orderFileInfo = new FileInfo(Path.Combine(orderDirInfo.FullName, orderDirInfo.Name + ".xml"));
            if (!orderFileInfo.Exists)
                return null;

            return new OrderHandler(orderDirInfo, orderFileInfo);
        }

        private static bool IsRequiredNonOrder(string fullPath)
        {
            // dir or pseudo dir, Mac specialty
            if (Path.EndsInDirectorySeparator(fullPath) || fullPath.Contains("__MACOSX", StringComparison.OrdinalIgnoreCase))
                return false;

            var ln = fullPath.ToLowerInvariant();

            foreach (var ds in new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar })
            {
                if (ln.Contains(ds + "backup" + ds)) // dental system backup
                    return false;
            }

            //-- IO scans: send both raw and non-raw if they exist. Back end determines what to use.

            if (ln.EndsWith("preparationscan.dcm") || ln.EndsWith("antagonistscan.dcm"))
                return true;

            if (ln.EndsWith("raw preparation scan.dcm") || ln.EndsWith("raw antagonist scan.dcm"))
                return true;

            if (ln.EndsWith("materials.xml") || ln.EndsWith("manufacturers.3ml") || ln.EndsWith("dentaldesignermodellingtree.3ml"))
                return true;

            return false;
        }


        /// <summary>
        /// get the contents of the order xml file as string
        /// </summary>
        /// <returns></returns>
        public string GetOrderText()
        {
            if (string.IsNullOrEmpty(_orderText))
                _orderText = File.ReadAllText(_orderFileInfo.FullName);
            return _orderText;
        }

        public bool IsScannedStatus()
        {
            if (string.IsNullOrEmpty(_orderText))
                _orderText = File.ReadAllText(_orderFileInfo.FullName);

            try
            {
                XDocument doc;
                using (TextReader sr = new StringReader(_orderText))
                    doc = XDocument.Load(sr);

                // find the first type = TDM_Item_ModelElement node
                if (!(doc.Root?.Descendants().FirstOrDefault(n => n.Attribute("type")?.Value == "TDM_Item_ModelElement")
                    is XElement meItem))
                    return false;

                if (!(meItem.Descendants().FirstOrDefault(n => n.Attribute("name")?.Value == "ProcessStatusID") is XElement manId))
                    return false;

                return manId.Attribute("value")?.Value == "psScanned";
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// pack all relevant order files in a zip archive, as stream. Is set to Position 0. Caller must dispose.
        /// Does not add preferences. Back end will use user's.
        /// </summary>
        public MemoryStream ZipOrderFiles()
        {
            var res = new MemoryStream();
            using (var newArchive = new ZipArchive(res, ZipArchiveMode.Create, true))
            {
                void AddEntry(FileInfo ofi)
                {
                    // careful with sub-directories (scans)
                    var fullPath = ofi.FullName.Replace("\\", "/"); // zip standard is '/'
                    var i = fullPath.IndexOf(OrderId, StringComparison.OrdinalIgnoreCase);
                    if (i < 0)
                        throw new Exception("Cannot find order name in path of file to zip");
                    var newName = fullPath.Substring(i);
                    var newEntry = newArchive.CreateEntry(newName);
                    using (var fromStream = new FileStream(ofi.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    using (var toStream = newEntry.Open())
                        fromStream.CopyTo(toStream);
                }

                AddEntry(_orderFileInfo);

                foreach (var fi in _orderDirectoryInfo.EnumerateFiles("*", SearchOption.AllDirectories))
                {
                    if (IsRequiredNonOrder(fi.FullName)) // order: must be exactly same, case sensitive
                        AddEntry(fi);
                }
            }

            res.Seek(0, SeekOrigin.Begin);
            return res;
        }
    }
}
