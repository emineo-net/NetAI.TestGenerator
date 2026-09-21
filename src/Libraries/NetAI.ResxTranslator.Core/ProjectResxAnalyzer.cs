using NetAI.ResxTranslator.Core.Models;
using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace NetAI.ResxTranslator.Core
{
    public class ProjectResxAnalyzer
    {
        public List<string> FindResxFiles(string projectDir)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(projectDir) || !Directory.Exists(projectDir))
                {
                    return new List<string>();
                }

                return Directory.GetFiles(projectDir, "*.resx", SearchOption.AllDirectories)
                    .Where(file =>
                        !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") &&
                        !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") &&
                        !file.Contains($"{Path.DirectorySeparatorChar}.git{Path.DirectorySeparatorChar}"))
                    .ToList();
            }
            catch (Exception)
            {
                return new List<string>();
            }
        }

        //public string DetermineDefaultLanguage(string projectDir)
        //{
        //    const string globalFallback = "en";
        //    try
        //    {
        //        if (string.IsNullOrWhiteSpace(projectDir) || !Directory.Exists(projectDir))
        //        {
        //            return globalFallback;
        //        }

        //        var csprojFile = Directory.GetFiles(projectDir, "*.csproj").FirstOrDefault();
        //        if (csprojFile != null && File.Exists(csprojFile))
        //        {
        //            var doc = XDocument.Load(csprojFile);
        //            var neutralLanguageElement = doc.Descendants("NeutralLanguage").FirstOrDefault();

        //            if (neutralLanguageElement != null && !string.IsNullOrWhiteSpace(neutralLanguageElement.Value))
        //            {
        //                string val = neutralLanguageElement.Value.Trim();
        //                if (IsValidCultureCode(val))
        //                {
        //                    return val;
        //                }
        //            }
        //        }
        //    }
        //    catch { /* fallback */ }

        //    return globalFallback;
        //}

        //public List<string> DetermineSupportedLanguages(string projectDir, Action<string>? logInfo = null)
        //{
        //    var result = new List<string>();
        //    try
        //    {
        //        if (string.IsNullOrWhiteSpace(projectDir) || !Directory.Exists(projectDir))
        //        {
        //            logInfo?.Invoke($"[AI-Translator Error] Project directory '{projectDir}' does not exist.");
        //            return result;
        //        }

        //        var csprojFile = Directory.GetFiles(projectDir, "*.csproj").FirstOrDefault();
        //        if (csprojFile == null || !File.Exists(csprojFile))
        //        {
        //            logInfo?.Invoke($"[AI-Translator] No .csproj file found in '{projectDir}' - <SupportedLanguage> could not be read.");
        //            return result;
        //        }

        //        var doc = XDocument.Load(csprojFile);

        //        var element = doc.Descendants("SupportedLanguage").FirstOrDefault()
        //                      ?? doc.Descendants("SupportedLanguages").FirstOrDefault();

        //        if (element == null || string.IsNullOrWhiteSpace(element.Value))
        //        {
        //            logInfo?.Invoke($"[AI-Translator] No <SupportedLanguage> tag found in '{Path.GetFileName(csprojFile)}' or it is empty.");
        //            logInfo?.Invoke($"[AI-Translator Warning] No <SupportedLanguage> tag found in '{Path.GetFileName(csprojFile)}' or it is empty.");
        //            return result;
        //        }

        //        var rawLanguages = element.Value.Split(',');
        //        foreach (var lang in rawLanguages)
        //        {
        //            string trimmed = lang.Trim();
        //            if (trimmed.Length > 0)
        //            {
        //                if (IsValidCultureCode(trimmed))
        //                {
        //                    if (!result.Contains(trimmed, StringComparer.OrdinalIgnoreCase))
        //                    {
        //                        result.Add(trimmed);
        //                    }
        //                }
        //                else
        //                {
        //                    logInfo?.Invoke($"[AI-Translator Warning] Invalid language code '{trimmed}' in <SupportedLanguage> tag ignored.");
        //                }
        //            }
        //        }
        //    }
        //    catch (XmlException ex)
        //    {
        //        logInfo?.Invoke($"[AI-Translator Error] The .csproj file is not valid XML: {ex.Message}");
        //    }
        //    // NEU: Lässt unsere spezifische Exception unverändert nach oben durchschlagen
        //    catch (InvalidDataException)
        //    {
        //        throw;
        //    }
        //    catch (Exception ex)
        //    {
        //        logInfo?.Invoke($"[AI-Translator Error] Error reading <SupportedLanguage>: {ex.Message}");
        //    }

        //    return result;
        //}


        public List<ResxFileInfo> ReadResxFiles(List<string> resxFilePaths, Action<string>? logInfo = null)
        {
            var result = new List<ResxFileInfo>();

            foreach (var path in resxFilePaths)
            {
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                {
                    logInfo?.Invoke($"[AI-Translator Warning] File skipped, path does not exist: '{path}'");
                    continue;
                }

                ResxFileInfo resxFile;
                try
                {
                    resxFile = new ResxFileInfo(path);
                    if (resxFile.Entries == null)
                    {
                        resxFile.Entries = new List<ResxEntry>();
                    }
                }
                catch (Exception ex)
                {
                    logInfo?.Invoke($"[AI-Translator Error] Failed to instantiate ResxFileInfo for '{path}': {ex.Message}");
                    continue;
                }

                try
                {
                    var doc = XDocument.Load(path);
                    var dataElements = doc.Descendants("data");

                    foreach (var element in dataElements)
                    {
                        var nameAttribute = element.Attribute("name")?.Value;
                        if (string.IsNullOrEmpty(nameAttribute)) continue;

                        var valueElement = element.Element("value")?.Value ?? string.Empty;
                        var commentElement = element.Element("comment")?.Value ?? string.Empty;

                        resxFile.Entries.Add(new ResxEntry
                        {
                            Key = nameAttribute,
                            Value = valueElement,
                            Comment = commentElement,
                            HasTranslation = !string.IsNullOrWhiteSpace(valueElement)
                        });
                    }

                    result.Add(resxFile);
                }
                catch (XmlException ex)
                {
                    logInfo?.Invoke($"[AI-Translator Error] The file '{Path.GetFileName(path)}' is corrupted (invalid XML): {ex.Message}");
                }
                catch (Exception ex)
                {
                    logInfo?.Invoke($"[AI-Translator Error] Unexpected error while reading '{Path.GetFileName(path)}': {ex.Message}");
                }
            }

            return result;
        }

        public void SaveTranslations(string filePath, List<ResxEntry> translatedEntries, Action<string>? logInfo = null)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
                {
                    logInfo?.Invoke($"[AI-Translator Error] File to save not found: '{filePath}'");
                    return;
                }

                if (translatedEntries == null || translatedEntries.Count == 0) return;

                var doc = XDocument.Load(filePath);
                var root = doc.Root;
                if (root == null)
                {
                    logInfo?.Invoke($"[AI-Translator Error] File '{Path.GetFileName(filePath)}' has no XML root element.");
                    return;
                }

                foreach (var translation in translatedEntries)
                {
                    var dataElement = root.Descendants("data")
                        .FirstOrDefault(e => e.Attribute("name")?.Value == translation.Key);

                    if (dataElement != null)
                    {
                        var valueElement = dataElement.Element("value");
                        if (valueElement != null)
                        {
                            valueElement.Value = translation.Value;
                        }
                        else
                        {
                            dataElement.Add(new XElement("value", translation.Value));
                        }
                    }
                    else
                    {
                        var newDataElement = new XElement("data",
                            new XAttribute("name", translation.Key),
                            new XAttribute(XNamespace.Xml + "space", "preserve"),
                            new XElement("value", translation.Value)
                        );
                        root.Add(newDataElement);
                    }
                }

                doc.Save(filePath);
            }
            catch (XmlException ex)
            {
                logInfo?.Invoke($"[AI-Translator Error] Saving failed, XML of '{Path.GetFileName(filePath)}' is corrupt: {ex.Message}");
            }
            catch (Exception ex)
            {
                logInfo?.Invoke($"[AI-Translator Error] Error writing to '{Path.GetFileName(filePath)}': {ex.Message}");
            }
        }

        public string GetBaseResourceName(string fileName)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(fileName)) return string.Empty;

                string withoutExtension = Path.GetFileNameWithoutExtension(fileName);
                string[] parts = withoutExtension.Split('.');

                if (parts.Length > 1 && IsValidCultureCode(parts[^1]))
                {
                    return string.Join(".", parts.Take(parts.Length - 1));
                }

                return withoutExtension;
            }
            catch
            {
                return fileName?.Split('.')[0] ?? string.Empty;
            }
        }

        public string GetLanguageFromFileName(string filePath)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(filePath)) return "neutral";

                string fileNameWithoutExtension = Path.GetFileNameWithoutExtension(filePath);
                string[] parts = fileNameWithoutExtension.Split('.');

                if (parts.Length > 1)
                {
                    string potentialCulture = parts[^1];
                    if (IsValidCultureCode(potentialCulture))
                    {
                        return potentialCulture;
                    }
                }
            }
            catch
            {
                // ignore
            }
            return "neutral";
        }

        public List<ResxFileInfo> EnsureSupportedLanguageFiles(
            List<ResxFileInfo> parsedResources,
            string projectDefaultLang,
            List<string> supportedLanguages,
            Action<string>? logInfo = null)
        {
            var newFiles = new List<ResxFileInfo>();

            try
            {
                if (supportedLanguages == null || supportedLanguages.Count == 0 || parsedResources == null || parsedResources.Count == 0)
                {
                    return newFiles;
                }

                var groups = parsedResources.GroupBy(f => GetBaseResourceName(f.FileName));

                foreach (var group in groups)
                {
                    string baseName = group.Key;
                    var filesInGroup = group.ToList();

                    var existingLanguages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var file in filesInGroup)
                    {
                        string lang = GetLanguageFromFileName(file.FilePath);
                        existingLanguages.Add(lang == "neutral" ? projectDefaultLang : lang);
                    }

                    var sourceFile = filesInGroup
                        .OrderByDescending(f => f.Entries?.Count(e => !string.IsNullOrWhiteSpace(e.Value)) ?? 0)
                        .FirstOrDefault();

                    if (sourceFile == null || !File.Exists(sourceFile.FilePath)) continue;

                    foreach (var targetLang in supportedLanguages)
                    {
                        if (existingLanguages.Contains(targetLang)) continue;

                        try
                        {
                            string dir = Path.GetDirectoryName(sourceFile.FilePath) ?? string.Empty;
                            string newFileName = $"{baseName}.{targetLang}.resx";
                            string newFilePath = Path.Combine(dir, newFileName);

                            logInfo?.Invoke($"[AI-Translator] Creating new language file: '{newFileName}' using the header metadata from '{Path.GetFileName(sourceFile.FilePath)}'");

                            var newDocument = XDocument.Load(sourceFile.FilePath);
                            var root = newDocument.Root;
                            if (root == null) continue;

                            root.Descendants("data").Remove();

                            if (sourceFile.Entries != null)
                            {
                                foreach (var entry in sourceFile.Entries)
                                {
                                    root.Add(new XElement("data",
                                        new XAttribute("name", entry.Key),
                                        new XAttribute(XNamespace.Xml + "space", "preserve"),
                                        new XElement("value", string.Empty)
                                    ));
                                }
                            }

                            newDocument.Save(newFilePath);

                            var nFile = new ResxFileInfo(newFilePath)
                            {
                                Entries = sourceFile.Entries?.Select(e => new ResxEntry
                                {
                                    Key = e.Key,
                                    Value = string.Empty,
                                    HasTranslation = false
                                }).ToList() ?? new List<ResxEntry>()
                            };

                            newFiles.Add(nFile);
                        }
                        catch (Exception ex)
                        {
                            logInfo?.Invoke($"[AI-Translator Error] Could not create language file for '{targetLang}' in group '{baseName}': {ex.Message}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                logInfo?.Invoke($"[AI-Translator Error] Error in EnsureSupportedLanguageFiles: {ex.Message}");
            }

            return newFiles;
        }

        private bool IsValidCultureCode(string cultureCode) => TranslatorLanguageResolver.IsValidCultureCode(cultureCode);
    }
}