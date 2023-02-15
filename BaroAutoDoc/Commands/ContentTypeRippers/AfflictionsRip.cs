﻿using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using BaroAutoDoc.SyntaxWalkers;

namespace BaroAutoDoc.Commands.ContentTypeSpecific;

sealed class AfflictionsRip : Command
{
    private readonly record struct AfflictionSection(Page.Section Section, ImmutableArray<(string, string, string)> ElementTable, ParsedType Parser);

    public void Invoke()
    {
        Directory.SetCurrentDirectory(GlobalConfig.RepoPath);
        const string srcPathFmt = "Barotrauma/Barotrauma{0}/{0}Source/Characters/Health/Afflictions/";
        string[] srcPathParams = { "Shared", "Client" };

        var contentTypeFinder = new AfflictionRipper();
        foreach (string p in srcPathParams)
        {
            string srcPath = string.Format(srcPathFmt, p);
            contentTypeFinder.VisitAllInDirectory(srcPath);
        }

        Directory.SetCurrentDirectory(Path.GetDirectoryName(Assembly.GetCallingAssembly().Location)!);

        Dictionary<string, ParsedType> parsedClasses = new();

        foreach (var (key, cls) in contentTypeFinder.AfflictionPrefabs)
        {
            if (parsedClasses.TryGetValue(key, out ParsedType? parser))
            {
                parser.ParseType(cls);
                continue;
            }

            parser = ParsedType.CreateParser(cls, new ClassParsingOptions
            {
                InitializerMethodNames = new[] { "LoadEffects" }
            });
            parser.ParseType(cls);
            parsedClasses.Add(key, parser);
        }

        foreach (var (key, parser) in parsedClasses)
        {
            Dictionary<string, AfflictionSection> finalSections = new();

            Page page = new()
            {
                Title = key
            };

            finalSections.Add(key, CreateSection(key, parser));

            WriteSubClasses(parser);

            void WriteSubClasses(ParsedType subParser)
            {
                foreach (var (subName, subSubParser) in subParser.SubClasses)
                {
                    finalSections.Add(subName, CreateSection(subName, subSubParser));
                    WriteSubClasses(subSubParser);
                }
            }

            foreach (var (identifier, section) in finalSections)
            {
                if (ConstructSubElementTable(finalSections, section.ElementTable, out Page.Section? table))
                {
                    section.Section.Subsections.Add(table);
                }

                if (key != identifier || parser.BaseClasses.Count is 0)
                {
                    page.Subsections.Add(section.Section);
                    AddEnumTable();
                    continue;
                }

                foreach (string type in parser.BaseClasses)
                {
                    if (!contentTypeFinder.AfflictionPrefabs.Keys.Any(k => string.Equals(k, type, StringComparison.OrdinalIgnoreCase))) { continue; }

                    section.Section.Body.Components.Add(new Page.RawText("This prefab also supports the attributes defined in: "));
                    section.Section.Body.Components.Add(new Page.Hyperlink($"{type}.md#{type.ToLower()}", type));
                }

                page.Subsections.Add(section.Section);
                AddEnumTable();

                void AddEnumTable()
                {
                    if (ConstructEnumTable(section.Parser.Enums, out ImmutableArray<Page.Section>? enums))
                    {
                        section.Section.Subsections.AddRange(enums);
                    }
                }
            }

            File.WriteAllText($"{key}.md", page.ToMarkdown());
        }

        static bool ConstructSubElementTable(Dictionary<string, AfflictionSection> sections, ImmutableArray<(string, string, string)> elementTable, [NotNullWhen(true)] out Page.Section? result)
        {
            if (!elementTable.Any())
            {
                result = null;
                return false;
            }

            Page.Section section = new()
            {
                Title = "Elements"
            };

            Page.Table table = new()
            {
                HeadRow = new Page.Table.Row("Element", "Type", "Description")
            };

            foreach (var (element, type, description) in elementTable)
            {
                if (string.IsNullOrWhiteSpace(type))
                {
                    Console.WriteLine($"WARNING: Element {element} has no type!");
                    continue;
                }

                string fmtType =
                    new Page.Hyperlink(
                        Url: sections.ContainsKey(type)
                            ? $"#{type.ToLower()}"
                            : $"{type}.md",
                        Text: type,
                        AltText: description)
                    .ToMarkdown();

                table.BodyRows.Add(new Page.Table.Row(element, fmtType, description));
            }

            if (table.BodyRows.Count is 0)
            {
                result = null;
                return false;
            }

            section.Body.Components.Add(table);

            result = section;
            return true;
        }

        static bool ConstructEnumTable(Dictionary<string, ImmutableArray<(string, string)>> enums, [NotNullWhen(true)] out ImmutableArray<Page.Section>? result)
        {
            if (!enums.Any())
            {
                result = null;
                return false;
            }

            var builder = ImmutableArray.CreateBuilder<Page.Section>();
            foreach (var (type, values) in enums)
            {
                Page.Section section = new()
                {
                    Title = type
                };

                Page.Table table = new()
                {
                    HeadRow = new Page.Table.Row("Value", "Description")
                };

                foreach (var (value, description) in values)
                {
                    table.BodyRows.Add(new Page.Table.Row(value, description));
                }

                section.Body.Components.Add(table);

                builder.Add(section);
            }

            result = builder.ToImmutable();
            return true;
        }

        static AfflictionSection CreateSection(string name, ParsedType parser)
        {
            Page.Section mainSection = new()
            {
                Title = name
            };

            foreach (CodeComment s in parser.Comments)
            {
                if (string.IsNullOrWhiteSpace(s.Text)) { continue; }
                mainSection.Body.Components.Add(new Page.RawText(s.Text));
            }

            Page.Section attributesSection = new()
            {
                Title = "Attributes"
            };

            Page.Table attributesTable = new()
            {
                HeadRow = new Page.Table.Row("Attribute", "Type", "Default value", "Description")
            };

            foreach (SerializableProperty property in parser.SerializableProperties)
            {
                string defaultValue = DefaultValue.MakeMorePresentable(property.DefaultValue, property.Type);

                attributesTable.BodyRows.Add(new Page.Table.Row(property.Name, property.Type, defaultValue, property.Description));
            }

            foreach (XMLAssignedField field in parser.XMLAssignedFields)
            {
                attributesTable.BodyRows.Add(new Page.Table.Row(field.XMLIdentifier, field.Field.Type, field.GetDefaultValue(), field.Field.Description));
            }

            if (attributesTable.BodyRows.Any())
            {
                attributesSection.Body.Components.Add(attributesTable);
                mainSection.Subsections.Add(attributesSection);
            }

            List<(string, string, string)> elementTable = new();

            foreach (SupportedSubElement affectedElement in parser.SupportedSubElements)
            {
                if (affectedElement.AffectedField.Length is 0) { continue; }

                // TODO we probably need to generate a list of all these elements
                // for example sprite, sound, effect
                DeclaredField field = affectedElement.AffectedField.First();
                elementTable.Add((affectedElement.XMLName, field.Type, field.Description));
            }

            return new AfflictionSection(mainSection, elementTable.ToImmutableArray(), parser);
        }
    }
}