using System;
using System.Collections.Generic;
using System.Text;
using System.IO;
using Newtonsoft.Json;

namespace DentalManagerPlugin
{
    /// <summary>
    /// POCO
    /// </summary>
    public class AppSettings
    {
        public bool AutoUpload { get; set; }

        /// <summary> pref for window placement </summary>
        public double PluginWindowLeft { get; set; }

        /// <summary> pref for window placement </summary>
        public double PluginWindowTop { get; set; }

        /// <summary> pref for window placement </summary>
        public double PluginWindowWidth { get; set; }

        /// <summary> pref for window placement </summary>
        public double PluginWindowHeight { get; set; }

        /// <summary>batch. typically c:\3shape </summary>
        public string OrdersRootDirectory { get; set; }

        /// <summary>batch. how long back do we consider orders </summary>
        public double BatchPeriodInHours { get; set; }

        public bool UseTestingServer { get; set; }

        /// <summary>
        /// return effective URI. always localhost in debug unless <paramref name="useProductionInDebug"/> is true
        /// </summary>
        public Uri GetUri(bool useProductionInDebug = false)
        {
            var s = UseTestingServer ? "fcexpressfront-testing.azurewebsites.net" : "express.fullcontour.com";
#if DEBUG
            if ( !useProductionInDebug)
                s = "localhost:44334";
#endif
            return new Uri("https://" + s);
        }

        public string GetAnyTestingInfo()
        {
            var s = UseTestingServer ? " TESTING" : "";
#if DEBUG
            s = " LOCALHOST";
#endif
            return s;
        }

        /// <summary>
        /// same as ID settings
        /// </summary>
        private static string AppDataDir => IdSettings.AppDataDir;

        private static string Filepath => Path.Combine(AppDataDir, "Settings.json");

        /// <summary>
        /// store <paramref name="sets"/> in AppData as protected json
        /// </summary>
        public static void Write(AppSettings appSettings)
        {
            var s = JsonConvert.SerializeObject(appSettings);

            if (!Directory.Exists(AppDataDir))
                Directory.CreateDirectory(AppDataDir);

            File.WriteAllText(Filepath, s);
        }

        /// <summary>
        /// read any from protected json file in AppData. Return null if no such json file.
        /// </summary>
        private static AppSettings Read()
        {
            if (!Directory.Exists(AppDataDir) || !File.Exists(Filepath))
                return null;

            var s = File.ReadAllText(Filepath);
            var sets = JsonConvert.DeserializeObject<AppSettings>(s);
            return sets;
        }

        /// <summary>
        /// read any from json file in AppData. Return new Settings if no such json file or any error.
        /// </summary>
        public static AppSettings ReadOrNew()
        {
            try
            {
                var sets = Read();
                return sets ?? new AppSettings();
            }
            catch (Exception)
            {
                return new AppSettings();
            }
        }
    }
}
