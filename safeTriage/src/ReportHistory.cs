using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml;
using System.Xml.Linq;

namespace SafeTriage
{
    internal sealed class SavedReport
    {
        public string Category { get; set; }
        public string Target { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public string Content { get; set; }

        public string DisplayName
        {
            get { return CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") + " · " + Category +
                (string.IsNullOrEmpty(Target) ? "" : " · " + Target); }
        }
    }

    internal sealed class ReportHistory
    {
        private readonly string directory;

        public ReportHistory(string directory)
        {
            this.directory = directory;
        }

        public void Save(SavedReport entry)
        {
            Directory.CreateDirectory(directory);
            string name = entry.CreatedAt.UtcDateTime.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) +
                "-" + Guid.NewGuid().ToString("N") + ".xml";
            var content = new XElement("SafeTriageReport",
                new XElement("Category", entry.Category),
                new XElement("Target", entry.Target ?? ""),
                new XElement("CreatedAt", entry.CreatedAt.ToString("o", CultureInfo.InvariantCulture)),
                new XElement("Content", entry.Content));
            ReportOutput.Save(Path.Combine(directory, name), content.ToString(SaveOptions.DisableFormatting));
        }

        public SavedReport[] Read(out int skipped)
        {
            skipped = 0;
            var entries = new List<SavedReport>();
            try
            {
                foreach (string file in Directory.EnumerateFiles(directory, "*.xml"))
                {
                    try
                    {
                        using (var reader = XmlReader.Create(file, new XmlReaderSettings
                        {
                            DtdProcessing = DtdProcessing.Prohibit, MaxCharactersInDocument = 1024 * 1024
                        }))
                        {
                            XElement item = XElement.Load(reader, LoadOptions.PreserveWhitespace);
                            string category = (string)item.Element("Category");
                            string content = (string)item.Element("Content");
                            DateTimeOffset time;
                            if (item.Name != "SafeTriageReport" || string.IsNullOrWhiteSpace(category) || content == null ||
                                !DateTimeOffset.TryParseExact((string)item.Element("CreatedAt"), "o", CultureInfo.InvariantCulture,
                                    DateTimeStyles.None, out time))
                            {
                                skipped++;
                                continue;
                            }
                            entries.Add(new SavedReport
                            {
                                Category = category, Target = (string)item.Element("Target"),
                                CreatedAt = time, Content = content
                            });
                        }
                    }
                    catch (IOException) { skipped++; }
                    catch (UnauthorizedAccessException) { skipped++; }
                    catch (XmlException) { skipped++; }
                }
            }
            catch (DirectoryNotFoundException) { }
            return entries.OrderByDescending(entry => entry.CreatedAt).ToArray();
        }
    }
}
