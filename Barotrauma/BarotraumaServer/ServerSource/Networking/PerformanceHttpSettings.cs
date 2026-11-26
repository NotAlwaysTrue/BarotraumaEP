using System;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Xml;
using System.Xml.Linq;

namespace Barotrauma.Networking
{
    // Dedicated-server configuration; never part of the game's network settings.
    internal sealed class PerformanceHttpSettings
    {
        public const string FileName = "HTTPPanelSettings.xml";
        private readonly XElement element;

        public PerformanceHttpSettings(XElement source = null)
        {
            element = source == null ? new XElement("PerformanceHttp") : new XElement(source);
            SetDefault("enabled", "false");
            SetDefault("host", "*");
            SetDefault("port", "8080");
            SetDefault("token", "");
            SetDefault(nameof(TokenEnabled), "true");
            SetDefault(nameof(EnableManagement), "false");
        }

        public bool Enabled => bool.TryParse(element.Attribute("enabled")?.Value, out bool enabled) && enabled;
        public string Token => element.Attribute("token")?.Value ?? "";
        public bool TokenEnabled => bool.TryParse(element.Attribute(nameof(TokenEnabled))?.Value, out bool enabled) && enabled;
        public bool EnableManagement => bool.TryParse(element.Attribute(nameof(EnableManagement))?.Value, out bool enabled) && enabled;

        private void SetDefault(string name, string value)
        {
            if (element.Attribute(name) == null)
            {
                element.SetAttributeValue(name, value);
            }
        }

        public XElement Save() => new XElement(element);

        public static PerformanceHttpSettings LoadOrCreate(string path)
        {
            if (!System.IO.File.Exists(path))
            {
                var defaults = new PerformanceHttpSettings();
                defaults.SaveFile(path, overwrite: false);
            }
            using var reader = XmlReader.Create(path, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = 65536
            });
            var document = XDocument.Load(reader);
            if (document.Root?.Name != "PerformanceHttp")
            {
                throw new FormatException("Expected a PerformanceHttp root element.");
            }
            return new PerformanceHttpSettings(document.Root);
        }

        public void SaveFile(string path, bool overwrite = true)
        {
            string temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                new XDocument(Save()).Save(temporaryPath);
                try
                {
                    System.IO.File.Move(temporaryPath, path, overwrite);
                }
                catch (System.IO.IOException) when (!overwrite && System.IO.File.Exists(path))
                {
                    // Another process created the defaults first; load its file.
                }
            }
            finally
            {
                if (System.IO.File.Exists(temporaryPath))
                {
                    System.IO.File.Delete(temporaryPath);
                }
            }
        }

        public bool EnsureToken()
        {
            if (!TokenEnabled || !string.IsNullOrWhiteSpace(Token))
            {
                return false;
            }
            element.SetAttributeValue("token", Convert.ToHexString(RandomNumberGenerator.GetBytes(32)));
            return true;
        }

        public bool TryGetPrefix(out string prefix, out string error)
        {
            prefix = "";
            error = "";
            if (!bool.TryParse(element.Attribute("enabled")?.Value, out _))
            {
                error = "PerformanceHttp enabled must be true or false.";
                return false;
            }
            if (!bool.TryParse(element.Attribute(nameof(TokenEnabled))?.Value, out _))
            {
                error = $"{nameof(TokenEnabled)} must be true or false.";
                return false;
            }
            if (!bool.TryParse(element.Attribute(nameof(EnableManagement))?.Value, out _))
            {
                error = $"{nameof(EnableManagement)} must be true or false.";
                return false;
            }
            if (!int.TryParse(element.Attribute("port")?.Value, NumberStyles.None, CultureInfo.InvariantCulture, out int port)
                || port < 1 || port > 65535)
            {
                error = "PerformanceHttp port must be between 1 and 65535.";
                return false;
            }
            string host = element.Attribute("host")?.Value ?? "";
            if (host == "0.0.0.0" || host == "::")
            {
                host = "*";
            }
            if (host != "*" && host != "+")
            {
                if (IPAddress.TryParse(host, out var address))
                {
                    host = address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6
                        ? $"[{address}]" : address.ToString();
                }
                else if (Uri.CheckHostName(host) != UriHostNameType.Dns)
                {
                    error = "PerformanceHttp host must be an IP address, hostname, * or +.";
                    return false;
                }
            }
            prefix = $"http://{host}:{port}/";
            return true;
        }
    }
}
