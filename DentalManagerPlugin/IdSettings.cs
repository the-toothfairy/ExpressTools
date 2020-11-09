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
        [JsonIgnore]
        internal IDataProtector Protector { get; set; }


        /// <summary>
        /// not allowed, use <see cref="ReadOrNew"/>
        /// </summary>
        private IdSettings()
        {
        }

        public Cookie AuthCookie { get; set; }

        public string UserLogin { get; set; }

        public static string AppDataDir =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ExpressDmPlugin");

        private static string ProtectedFileFullPath => Path.Combine(AppDataDir, "dmp.xjs");

        /// <summary>
        /// store <paramref name="sets"/> in AppData as protected json
        /// </summary>
        public void Write()
        {
            var s = JsonConvert.SerializeObject(this);
            var sProtected = Protector.Protect(s);

            if (!Directory.Exists(AppDataDir))
                Directory.CreateDirectory(AppDataDir);

            File.WriteAllText(ProtectedFileFullPath, sProtected);
        }

        /// <summary>
        /// read any from json file in AppData. Return new Settings if no such json file or any error.
        /// </summary>
        public static IdSettings ReadOrNew()
        {
            var serviceCollection = new ServiceCollection();
            serviceCollection.AddDataProtection();
            var services = serviceCollection.BuildServiceProvider();
            var protector = services.GetDataProtector("expresstools");

            if (!Directory.Exists(AppDataDir))
            {
                Directory.CreateDirectory(AppDataDir);
                return new IdSettings { Protector = protector };
            }

            if ( !File.Exists(ProtectedFileFullPath))
                return new IdSettings { Protector = protector };

            var sProtected = File.ReadAllText(ProtectedFileFullPath);
            var s = protector.Unprotect(sProtected);
            var sets = JsonConvert.DeserializeObject<IdSettings>(s);

            var res = sets ?? new IdSettings();
            res.Protector = protector;
            return res;
        }
    }
}
