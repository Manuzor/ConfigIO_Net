﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace Configuration.FileIO
{
    public interface IConfigFileReader
    {
        ConfigFileReaderCallbacks Callbacks { get; set; }

        SyntaxMarkers Markers { get; set; }

        bool NormalizeLineEndings { get; set; }

        ConfigFile Parse(TextReader reader);
    }

    public enum ReadStep
    {
        ReadName,
        ReadOptionValue,
        ReadSectionBody,
        ReadComment,
    }

    /// <summary>
    /// Parses only the body of a section, not its identifier/header.
    /// </summary>
    /// <example>
    /// In the following sample, Option0, Option1, and Option2 are all parsed by a different instance of this class.
    /// <code>
    /// Option0 = Value0
    /// Section:
    ///     Option1 = Value1
    ///     InnerSection:
    ///         Option2 = Value2
    /// </code>
    /// </example>
    public class ConfigFileReader : IConfigFileReader
    {
        public struct SectionInfo
        {
            public ConfigSection Section { get; set; }
            public int Indentation { get; set; }
        }

        /// <summary>
        /// Set of markers to determine syntax.
        /// Should be set by the user creating an instace of this class.
        /// </summary>
        public SyntaxMarkers Markers { get; set; }

        public ConfigFileReaderCallbacks Callbacks { get; set; }

        public bool NormalizeLineEndings { get; set; }

        public ConfigFileReader()
        {
            Callbacks = new ConfigFileReaderCallbacks()
                        {
                            SectionNameProcessor = section => section.Trim(),
                            OptionValueProcessor = value => value.Trim(),
                            OptionNameProcessor =  name => name.Trim(),
                            FileNameProcessor =    fileName => fileName.Trim(),
                        };
            NormalizeLineEndings = true;
        }

        public ConfigFile Parse(TextReader reader)
        {
            var content = reader.ReadToEnd();

            if (NormalizeLineEndings)
            {
                content = content.Replace("\r", string.Empty);
            }

            var stream = new StringStream(content);

            var cfg = new ConfigFile();
            ParseSection(stream, new SectionInfo()
                                 {
                                     Section = cfg,
                                     Indentation = -1,
                                 });

            return cfg;
        }

        public ConfigFile Parse(StringStream stream)
        {
            if (NormalizeLineEndings)
            {
                var normalizedContent = stream.CurrentContent.Replace("\r", string.Empty);
                stream = new StringStream(normalizedContent);
            }

            var cfg = new ConfigFile();
            ParseSection(stream, new SectionInfo()
                                 {
                                     Section = cfg,
                                     Indentation = -1,
                                 });

            return cfg;
        }

        private void ParseSection(StringStream stream, SectionInfo parent)
        {
            SkipWhiteSpaceAndComments(stream);

            if (stream.IsAtEndOfStream) { return; }

            // Determine the indentation for this section.
            var sectionIndentation = DetermineLineIndentation(stream);
            var currentIndentation = sectionIndentation;

            Debug.Assert(sectionIndentation != parent.Indentation);

            while (true)
            {
                if (stream.IsAtEndOfStream) { break; }

                if (currentIndentation <= parent.Indentation)
                {
                    // We have something like this:
                    //    | Section0:
                    //    |     opt = val
                    // -> | Section1:
                    //    |     ...
                    break;
                }
                else if (currentIndentation != sectionIndentation)
                {
                    //    | Section0:
                    //    |     opt0 = val0
                    //    |     opt1 = val1
                    // -> |       opt2 = val2
                    // -> |         Section1:
                    Error(stream, new InvalidIndentationException(currentIndentation, sectionIndentation));
                }

                var name = ParseName(stream);

                if (stream.IsAt(Markers.KeyValueDelimiter))
                {
                    // We are at:
                    // Option = Value
                    // -------^

                    stream.Next(Markers.KeyValueDelimiter.Length);
                    CheckStream(stream, ReadStep.ReadOptionValue);

                    // We are at the value of an option.
                    var value = ParseValue(stream);

                    if (name.TrimStart().StartsWith(Markers.IncludeBeginMarker))
                    {
                        // We are at something like:
                        // [include] SectionName = Path/To/File.cfg
                        // ----------------------------------------^
                        var fileName = Callbacks.FileNameProcessor(value);
                        var cfg = new ConfigFile()
                                  {
                                      Owner = parent.Section.Owner,
                                      FileName = fileName,
                                  };
                        cfg.Load();
                        // Remove "[include]" from the name
                        name = name.Replace(Markers.IncludeBeginMarker, string.Empty);
                        cfg.Name = Callbacks.SectionNameProcessor(name);
                        parent.Section.AddSection(cfg);
                    }
                    else
                    {
                        // We are at something like:
                        // Option = Value
                        // --------------^
                        var option = new ConfigOption()
                        {
                            Owner = parent.Section.Owner,
                            Name = Callbacks.OptionNameProcessor(name),
                            Value = Callbacks.OptionValueProcessor(value),
                        };
                        parent.Section.AddOption(option);
                    }
                }
                else if (stream.IsAt(Markers.SectionBodyBeginMarker))
                {
                    // We are at:
                    // SectionName:
                    // -----------^

                    stream.Next(Markers.SectionBodyBeginMarker.Length);

                    // We are at the beginning of a section body
                    var subSection = new ConfigSection() { Owner = parent.Section.Owner };
                    ParseSection(stream, new SectionInfo()
                                         {
                                             Section = subSection,
                                             Indentation = sectionIndentation
                                         });
                    subSection.Name = Callbacks.SectionNameProcessor(name);
                    parent.Section.AddSection(subSection);
                }
                else
                {
                    // Now we must be at an option that has no value.
                    var option = new ConfigOption()
                    {
                        Name = Callbacks.OptionNameProcessor(name),
                        Value = Callbacks.OptionValueProcessor(string.Empty),
                    };
                    parent.Section.AddOption(option);
                }

                SkipWhiteSpaceAndComments(stream);
                currentIndentation = DetermineLineIndentation(stream);
            }
        }

        private void ParseComment(StringStream stream)
        {
            CheckStream(stream, ReadStep.ReadComment);

            if (stream.IsAt(Markers.SingleLineCommentBeginMarker))
            {
                stream.SkipUntil(_ => stream.IsAtNewLine);
                stream.Next(stream.NewLine.Length);
                
            }
            else if (stream.IsAt(Markers.MultiLineCommentBeginMarker))
            {
                stream.SkipUntil(_ => stream.IsAt(Markers.MultiLineCommentEndMarker));
                stream.Next(Markers.MultiLineCommentEndMarker.Length);
            }
            else
            {
                Error(stream, new InvalidOperationException("Current stream is not at a comment!"));
            }
        }

        private string ParseName(StringStream stream)
        {
            var start = new StringStream(stream);
            var length = stream.SkipUntil(_ => stream.IsAtNewLine                                 // \n
                                            || stream.IsAt(Markers.KeyValueDelimiter)             // =
                                            || stream.IsAt(Markers.SectionBodyBeginMarker)        // :
                                            || stream.IsAt(Markers.SingleLineCommentBeginMarker)  // //
                                            || stream.IsAt(Markers.MultiLineCommentBeginMarker)); // /*

            var name = start.Content.Substring(start.Index, length);
            return name;
        }

        private string ParseValue(StringStream stream)
        {
            var start = new StringStream(stream);
            var length = stream.SkipUntil(_ => stream.IsAtNewLine                                 // \n
                                            || stream.IsAt(Markers.LongValueBeginMarker)          // "
                                            || stream.IsAt(Markers.SingleLineCommentBeginMarker)  // //
                                            || stream.IsAt(Markers.MultiLineCommentBeginMarker)); // /*

            if (stream.IsAt(Markers.LongValueBeginMarker))
            {
                stream.Next(Markers.LongValueBeginMarker.Length);

                start = new StringStream(stream);
                length = stream.SkipUntil(_ => stream.IsAt(Markers.LongValueEndMarker));

                if (!stream.IsAt(Markers.LongValueEndMarker))
                {
                    Error(stream, new InvalidSyntaxException(string.Format("Missing long-value end-marker \"{0}\".",
                                                                           Markers.LongValueEndMarker)));
                }

                stream.Next(Markers.LongValueEndMarker.Length);
            }

            var value = start.Content.Substring(start.Index, length);
            return value;
        }

        private void SkipWhiteSpaceAndComments(StringStream stream)
        {
            while (true)
            {
                stream.SkipWhile(c => char.IsWhiteSpace(c));

                if (stream.IsAt(Markers.SingleLineCommentBeginMarker)
                 || stream.IsAt(Markers.MultiLineCommentBeginMarker))
                {
                    ParseComment(stream);
                }
                else
                {
                    break;
                }
            }
        }

        private int DetermineLineIndentation(StringStream stream)
        {
            var copy = new StringStream(stream);

            // Skip back to the beginning of the line.
            copy.SkipReverseUntil(_ => copy.IsAtBeginning || copy.IsAtNewLine);

            // Skip ahead until we are no longer at the new line character
            copy.Next(copy.NewLine.Length);

            // Now skip all white space and return how much was skipped.
            return copy.SkipWhile(c => char.IsWhiteSpace(c));
        }

        private void CheckStream(StringStream stream, ReadStep step)
        {
            if (stream.IsValid) { return; }

            throw new NotImplementedException();
        }

        private void Error(StringStream currentStream, Exception inner)
        {
            throw new Exception(string.Format("Line {0}:",
                                              currentStream.CurrentLineNumber),
                                inner);
        }
    }

}
