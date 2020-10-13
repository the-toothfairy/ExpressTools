using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json;

namespace DentalManagerPlugin
{
    /// <summary>
    /// protected storage of login data (excluding password!, but any persistent cookie), and unprotected of associated info
    /// </summary>
    public class IdSettings
    {
        private static readonly IDataProtector Protector;

        static IdSettings()
        {
            var serviceCollection = new ServiceCollection();
            serviceCollection.AddDataProtection();
            var services = serviceCollection.BuildServiceProvider();
            Protector = services.GetDataProtector("expresstools");
        }

        public Cookie AuthCookie { get; set; }

        public string UserLogin { get; set; }

        public bool AutoUpload { get; set; }

        public string OrderDirectory { get; set; }


        private static string AppDataDir =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ExpressDmPlugin");

        private static string ProtectedFileFullPath => Path.Combine(AppDataDir, "dmp.xjs");

        private static string UrlFileFullPath => Path.Combine(AppDataDir, "url.txt");

        /// <summary>
        /// store <paramref name="sets"/> in AppData as protected json
        /// </summary>
        public static void Write(IdSettings sets)
        {
            var s = JsonConvert.SerializeObject(sets);
            var sProtected = Protector.Protect(s);

            if (!Directory.Exists(AppDataDir))
                Directory.CreateDirectory(AppDataDir);

            File.WriteAllText(ProtectedFileFullPath, sProtected);
        }

        /// <summary>
        /// read any from protected json file in AppData. Return null if no such json file.
        /// </summary>
        private static IdSettings Read()
        {
            if (!Directory.Exists(AppDataDir) || !File.Exists(ProtectedFileFullPath))
                return null;

            var sProtected = File.ReadAllText(ProtectedFileFullPath);
            var s = Protector.Unprotect(sProtected);
            var sets = JsonConvert.DeserializeObject<IdSettings>(s);
            return sets;
        }

        /// <summary>
        /// read any from json file in AppData. Return new Settings if no such json file or any error.
        /// </summary>
        public static IdSettings ReadOrNew()
        {
            try
            {
                var sets = Read();
                return sets ?? new IdSettings();
            }
            catch (Exception)
            {
                return new IdSettings();
            }
        }

        /// <summary>
        /// for development, or in case of emergency, allow reading a different url from unprotected, separate file (so user can change).
        /// Null if nothing (valid) found. Format: 'https://express.fullcontour.com'. File name: <see cref="UrlFileFullPath"/>
        /// </summary>
        private static Uri ReadAnyAssociatedUri()
        {
            if (!Directory.Exists(AppDataDir) || !File.Exists(UrlFileFullPath))
                return null;

            var s = File.ReadAllText(UrlFileFullPath);
            if (string.IsNullOrEmpty(s))
                return null;
            return Uri.TryCreate(s, UriKind.Absolute, out var res) ? res : null;
        }

        /// <summary>
        /// return effective URI
        /// </summary>
        public static Uri GetUri()
        {
            var uri = ReadAnyAssociatedUri(); // allow change
            if (uri == null)
            {
                uri = new Uri("https://express.fullcontour.com/");
#if DEBUG
                uri = new Uri("https://localhost:44334/");
#endif
            }
            return uri;
        }
    }
}
