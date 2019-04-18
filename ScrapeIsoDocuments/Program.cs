using HtmlAgilityPack;
using Newtonsoft.Json;
using Polly;
using Polly.Retry;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;

namespace ScrapeIsoDocuments
{
    internal class Program
    {
        /// <summary>
        /// All .json files will be placed in this subdirectory of the working directory.
        /// The directory will be cleaned on start.
        /// </summary>
        public const string OutputDirectory = "SpecRef";

        // Explicit listing because the naming is reverse engineered and oddball entries are sure to exist.
        // Review each catalog page manually before adding, in case parsing rules need to be adjusted.
        public static (string outfile, string url)[] CatalogPagesToScrape = new[]
        {
            // NB! Make sure the filter in the URL incldues withdrawn/deleted documents!
            // We just mark them as such, we do not delete from references (because legacy).

            // ISO/IEC JTC 1/SC 29 Coding of audio, picture, multimedia and hypermedia information
            ("iso_jtc1_sc29.json", "https://www.iso.org/committee/45316/x/catalogue/p/1/u/1/w/1/d/1")
        };

        // The ISO website tends to fail a lot. Just retry.
        private static RetryPolicy<string> ScrapePolicy = Policy<string>.Handle<Exception>().Retry(5);

        private sealed class Entry
        {
            // Used for cross-referencing purposes.
            // For regular documents, this is the document itself (e.g. 12345-6).
            // For addon documents, this is the base ID of the parent document.
            // Some documents might not have base ID at all! (Some could have unusual ID structure)
            public string BaseId { get; set; }

            public bool IsAddonDocument { get; set; }

            public int SortIndex { get; set; }

            public string Url { get; set; }

            public string Title { get; set; }
            public string Status { get; set; }
            public bool IsSuperseded { get; set; }
            public bool IsRetired { get; set; }
            public bool IsUnderDevelopment { get; set; }

            // May be null.
            public string IsoNumber { get; set; }

            // The ID of the entry that has caused this entry to be superseded/retired (if we can determine it).
            public string ObsoletedBy { get; set; }
        }

        // We just want the first 12345-56 looking string, that's it. There may be a year suffix, which we ignore.
        private static readonly Regex ExtractBaseIdRegex = new Regex(@"([\d-]+)", RegexOptions.Compiled);

        // We just want the first 12345-56:7890 looking string, that's it.
        private static readonly Regex ExtractIsoNumber = new Regex(@"([\d-]+:[\d-]+)", RegexOptions.Compiled);

        public static string TrimIsoPrefix(string title)
        {
            const string prefix = "ISO/IEC ";

            if (title.StartsWith(prefix))
                return title.Substring(prefix.Length);
            else
                return title;
        }

        public static bool DetermineIsAddonDocument(string title)
        {
            title = TrimIsoPrefix(title);

            return title.Contains('/');
        }

        public static string MakeIsoNumber(string title, bool isAddonDocument)
        {
            if (isAddonDocument)
                return null; // Addons do not have ISO numbers.

            title = TrimIsoPrefix(title);

            if (!title.Contains(":"))
                return null; // No year, no ISO number.

            var isoNumber = ExtractIsoNumber.Match(title);

            if (!isoNumber.Success)
                throw new Exception("Unexpected failure parsing ISO number: " + title);

            return "ISO " + isoNumber.Captures[0].Value;
        }

        public static (string baseId, string specrefId) MakeIds(string title, bool isAddonDocument)
        {
            title = TrimIsoPrefix(title);

            if (!isAddonDocument)
            {
                // It is a regular title.

                // ISO/IEC FDIS 21000-22
                // ISO/IEC 21000-22:2016

                var baseId = ExtractBaseIdRegex.Match(title);

                if (!baseId.Success)
                    throw new Exception("Failed to parse ID from title: " + title);

                var baseIdValue = baseId.Captures[0].Value;

                return (baseIdValue, "iso" + baseIdValue);
            }
            else
            {
                // It is an addon title.

                // ISO/IEC 21000-22:2016/Amd 1:2018

                // Same logic as regular, except we don't stop with the captured group.

                var baseId = ExtractBaseIdRegex.Match(title);

                if (!baseId.Success)
                    throw new Exception("Failed to parse ID from title: " + title);

                var baseIdValue = baseId.Captures[0].Value;
                var baseIdStart = baseId.Captures[0].Index;

                var id = title.Substring(baseIdStart)
                    .ToLowerInvariant()
                    .Replace(" ", "")
                    .Replace(":", "-")
                    .Replace("/", "-");

                return (baseIdValue, "iso" + id);
            }
        }

        private static void Main(string[] args)
        {
            // For each document, follow the main link https://www.iso.org/standard/66288.html?browse=tc
            // This link will also be the main link for the document, UNLESS it is one of the
            // "publically available standards" in which case TODO how to get the proper link.

            // ISO document life cycle stages (as shown on the website):
            // * Under development - new document OR new version of existing document. Lacks version tag. May have decorated name.
            // * Published - a published document, always with a specific version tag (":2005").
            // * Withdrawn - obsolete document; can mean that a new version of same document was published or that the document
            //      became irrelevant due to being merged into another document or just obsolescence.
            // * Deleted - a document that never got published and just got deleted at some point.

            // Parts of the document name indicate lifecycle stage and should be ignored.
            // For example, these are the same document, in different versions:
            // ISO/IEC FDIS 21000-22 [Under development]
            // ISO/IEC 21000-22:2016
            // Both are "iso21000-22" in SpecRef.
            // The latter is preferred over the former because the former is under development.

            // Some documents are addons for other documents. These are always specific to a version
            // and do not exist independently of base document version and addon version.
            // ISO/IEC 21000-22:2016/Amd 1:2018
            // This becomes "iso21000-22-2016-amd1-2018" in SpecRef.

            // We could extract more detailed info by looking at the page of each individual document but
            // for the moment, the cost/benefit ratio is good enough going by the main catalog page.

            if (Directory.Exists(OutputDirectory))
                Directory.Delete(OutputDirectory, true);
            Directory.CreateDirectory(OutputDirectory);

            Console.WriteLine("Output will be saved in " + Path.GetFullPath(OutputDirectory));

            var client = new HttpClient
            {
                // The ISO website can be quite slow.
                Timeout = TimeSpan.FromSeconds(300)
            };

            foreach ((var outfile, var pageUrl) in CatalogPagesToScrape)
            {
                var entries = new Dictionary<string, Entry>();

                var page = new HtmlDocument();

                Console.WriteLine($"Loading catalog page: {pageUrl}");

                var pageHtml = ScrapePolicy.Execute(() => client.GetStringAsync(pageUrl).Result);
                page.LoadHtml(pageHtml);

                var dataTable = page.GetElementbyId("datatable-tc-projects");
                var documents = dataTable.SelectNodes("tbody/tr/td[1]");

                Console.WriteLine($"Found {documents.Count} documents.");

                var documentIndex = 1;

                foreach (var document in documents)
                {
                    var title = document.SelectSingleNode("div/div/a")?.GetAttributeValue("title", null)?.Trim();
                    var relativeUrl = document.SelectSingleNode("div/div/a")?.GetAttributeValue("href", null);

                    // Optional. Some documents do not have it.
                    var summary = document.SelectSingleNode("div/div[@class='entry-summary']")?.InnerText
                        .Replace("\n", "")
                        .Replace("\r", "")
                        .Trim();

                    if (summary?.Length <= 3)
                        summary = null; // Sometimes the ISO website just has some punctuation there - ignore.

                    if (title == null || relativeUrl == null)
                        throw new Exception("Unable to parse document entry: " + document.InnerHtml);

                    var absoluteUrl = new Uri(new Uri(pageUrl), relativeUrl);

                    var isAddon = DetermineIsAddonDocument(title);
                    (var baseId, var id) = MakeIds(title, isAddon);
                    var isoNumber = MakeIsoNumber(title, isAddon);

                    var label = document.SelectSingleNode("div/div/span[contains(@class, 'small')]");

                    string status = "Published";
                    bool withdrawn = false;
                    bool deleted = false;
                    bool underDevelopment = false;

                    switch (label?.InnerText)
                    {
                        case "[Withdrawn]":
                            status = "Withdrawn";
                            withdrawn = true;
                            break;
                        case "[Under development]":
                            status = "Under development";
                            underDevelopment = true;
                            break;
                        case "[Deleted]":
                            status = "Deleted";
                            deleted = true;
                            break;
                        case null:
                            break;
                        default:
                            throw new Exception("Unexpected label: " + label.InnerText);
                    }

                    Console.WriteLine($"{title} [{status}] is titled \"{summary}\" and can be found at {absoluteUrl} and will get the ID {id}");

                    if (entries.ContainsKey(id))
                    {
                        if (entries[id].IsUnderDevelopment || entries[id].IsRetired || entries[id].IsSuperseded)
                        {
                            Console.WriteLine($"Overwriting {id} because this one is a more preferred version.");
                        }
                        else if (underDevelopment || withdrawn || deleted)
                        {
                            Console.WriteLine($"Skipping {id} because we already have a more preferred version.");
                            continue;
                        }
                        else
                        {
                            throw new Exception("Unexpected duplicate ID that does not seem to be a different version of the same document: " + id);
                        }
                    }

                    var entry = new Entry
                    {
                        // We use the same sorting as on the ISO website.
                        SortIndex = documentIndex++,

                        IsAddonDocument = isAddon,
                        BaseId = baseId,

                        IsSuperseded = withdrawn, // Maybe not always accurate translation but what can you do.
                        IsRetired = deleted,
                        IsUnderDevelopment = underDevelopment,
                        Status = status,
                        Title = summary ?? title, // Summary is optional on ISO website but we need something.
                        IsoNumber = isoNumber,
                        // We drop the query string because it seems needless.
                        Url = absoluteUrl.GetLeftPart(UriPartial.Path),
                    };

                    entries[id] = entry;
                }

                if (entries.Count == 0)
                    throw new Exception("Loaded no entries."); // Sanity check.

                // Try cross-reference entries so that superseded documents reference the new ones.
                // This is somewhat heuristics-based and the website actually has explicit references but "good enough" for now.
                // Consider parsing the explicit references on each document's individual page if you want to make it more proper.
                var publishedDocuments = entries.Where(pair => !pair.Value.IsSuperseded && !pair.Value.IsUnderDevelopment && !pair.Value.IsRetired);
                var notPublishedDocuments = entries.Except(publishedDocuments);

                foreach (var obsolete in notPublishedDocuments)
                {
                    // If we can find a valid non-addon document with the same BaseID, we reference it as the fresh version.
                    var newAndImproved = publishedDocuments.Where(pair => pair.Value.BaseId == obsolete.Value.BaseId && !pair.Value.IsAddonDocument).ToArray();

                    if (newAndImproved.Length > 1)
                        throw new Exception($"Found multiple new versions of {obsolete.Key}: {string.Join(", ", newAndImproved.Select(pair => pair.Key))}");

                    if (newAndImproved.Length == 0)
                        continue;

                    obsolete.Value.ObsoletedBy = newAndImproved.Single().Key;
                    Console.WriteLine($"Marking as obsoleted: {obsolete.Key} -> {obsolete.Value.ObsoletedBy}");
                }

                // Ok, we got all our entries. Serialize.
                var json = new Dictionary<string, object>(entries.Count);

                foreach (var pair in entries.OrderBy(p => p.Value.SortIndex))
                {
                    string[] obsoletedBy = null;
                    if (pair.Value.ObsoletedBy != null)
                        obsoletedBy = new[] { pair.Value.ObsoletedBy };

                    json[pair.Key] = new
                    {
                        href = pair.Value.Url,
                        title = pair.Value.Title,
                        status = pair.Value.Status,
                        publisher = "ISO/IEC",
                        isoNumber = pair.Value.IsoNumber,
                        isSuperseded = pair.Value.IsSuperseded,
                        isRetired = pair.Value.IsRetired,
                        obsoletedBy = obsoletedBy
                    };
                }

                var outputFilePath = Path.Combine(OutputDirectory, outfile);
                File.WriteAllText(outputFilePath, JsonConvert.SerializeObject(json, JsonSettings), OutputEncoding);
            }
        }

        private static readonly JsonSerializerSettings JsonSettings = new JsonSerializerSettings
        {
            Formatting = Formatting.Indented,
            DefaultValueHandling = DefaultValueHandling.Ignore
        };

        private static readonly Encoding OutputEncoding = new UTF8Encoding(false);
    }
}
