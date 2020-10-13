using System;
using System.Collections.Generic;
using System.Text;
using System.Windows.Media;

namespace DentalManagerPlugin
{
    /// <summary>
    /// common appearance specs
    /// </summary>
    public class Visual
    {
        /// <summary>
        /// "quality" of message in errors, for log, etc
        /// </summary>
        public enum Severities
        {
            Info = 0,
            Good,
            Warning,
            Error,
        }

        public static Dictionary<Severities, Color> MessageColors = new Dictionary<Severities, Color>
        {
            { Severities.Info, Colors.Black },
            { Severities.Good, Colors.Green },
            { Severities.Warning, Colors.DarkOrange },
            { Severities.Error, Colors.Red },
        };
    }
}
