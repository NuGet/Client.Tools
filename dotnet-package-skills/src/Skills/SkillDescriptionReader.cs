using System.Text;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;

namespace DotnetPackageSkills.Skills;

internal sealed record SkillDescriptionResult(string? Description, string? Warning);

internal static class SkillDescriptionReader
{
    private const int MaxFrontmatterCharacters = 64 * 1024;
    private const int MaxNestingDepth = 32;

    public static SkillDescriptionResult Read(string skillDirectory)
    {
        try
        {
            using var reader = new StreamReader(Path.Combine(skillDirectory, SkillDiscovery.SkillManifestFileName));
            var charactersRead = 0;
            if (!ReadOpeningDelimiter(reader, ref charactersRead))
            {
                return new(null, null);
            }

            var frontmatter = new StringBuilder();
            while (ReadBoundedLine(reader, ref charactersRead) is { } line)
            {
                if (line.TrimEnd(' ', '\t') is "---" or "...")
                {
                    return Parse(frontmatter.ToString());
                }

                frontmatter.Append(line).Append('\n');
            }

            return Warn("frontmatter has no closing delimiter; add a closing '---' line.");
        }
        catch (FileNotFoundException)
        {
            return Warn("was not found; restore the file to read its description.");
        }
        catch (DirectoryNotFoundException)
        {
            return Warn("was not found; restore the file to read its description.");
        }
        catch (UnauthorizedAccessException)
        {
            return Warn("could not be read; check file permissions.");
        }
        catch (IOException)
        {
            return Warn("could not be read; check the file path, permissions, and whether it is in use.");
        }
        catch (ArgumentException)
        {
            return Warn("has an invalid path; check the skill directory path.");
        }
        catch (YamlException)
        {
            return Warn("has malformed YAML frontmatter; fix the header.");
        }
        catch (FrontmatterException exception)
        {
            return Warn(exception.Message);
        }
    }

    private static bool ReadOpeningDelimiter(StreamReader reader, ref int charactersRead)
    {
        for (var index = 0; index < 3; index++)
        {
            if (ReadCharacter(reader, ref charactersRead) != '-')
            {
                return false;
            }
        }

        while (true)
        {
            switch (ReadCharacter(reader, ref charactersRead))
            {
                case -1:
                case '\n':
                    return true;
                case '\r':
                    ReadLineFeed(reader, ref charactersRead);
                    return true;
                case ' ':
                case '\t':
                    break;
                default:
                    return false;
            }
        }
    }

    private static string? ReadBoundedLine(StreamReader reader, ref int charactersRead)
    {
        var line = new StringBuilder();
        while (true)
        {
            var character = ReadCharacter(reader, ref charactersRead);
            switch (character)
            {
                case -1:
                    return line.Length == 0 ? null : line.ToString();
                case '\n':
                    return line.ToString();
                case '\r':
                    ReadLineFeed(reader, ref charactersRead);
                    return line.ToString();
                default:
                    line.Append((char)character);
                    break;
            }
        }
    }

    private static void ReadLineFeed(StreamReader reader, ref int charactersRead)
    {
        if (reader.Peek() == '\n')
        {
            ReadCharacter(reader, ref charactersRead);
        }
    }

    private static int ReadCharacter(StreamReader reader, ref int charactersRead)
    {
        var character = reader.Read();
        if (character >= 0 && ++charactersRead > MaxFrontmatterCharacters)
        {
            throw new FrontmatterException("frontmatter exceeds 64 KiB of text; shorten the header.");
        }

        return character;
    }

    private static SkillDescriptionResult Parse(string frontmatter)
    {
        try
        {
            return ParseDocument(frontmatter);
        }
        catch (SemanticErrorException exception) when (
            exception.Start.Index == frontmatter.Length && exception.End.Index == frontmatter.Length)
        {
            // YamlDotNet rejects indentation in an all-blank trailing block. Reparse the
            // whole document without trailing whitespace, keeping all metadata checks.
            return ParseDocument(frontmatter.TrimEnd(' ', '\t', '\n') + '\n');
        }
    }

    private static SkillDescriptionResult ParseDocument(string frontmatter)
    {
        var parser = new Parser(new StringReader(frontmatter));
        parser.Consume<StreamStart>();
        if (parser.TryConsume<StreamEnd>(out _))
        {
            return new(null, null);
        }

        parser.Consume<DocumentStart>();
        var root = ReadNodeStart(parser);
        if (root is not MappingStart)
        {
            throw new FrontmatterException("frontmatter must be a YAML mapping; use 'description: ...'.");
        }

        string? description = null;
        var foundDescription = false;
        while (!parser.TryConsume<MappingEnd>(out _))
        {
            var key = ReadNode(parser, 2);
            var value = ReadNode(parser, 2);
            if (key?.Value != "description")
            {
                continue;
            }

            if (foundDescription)
            {
                throw new FrontmatterException("has duplicate description keys; keep only one top-level description.");
            }

            foundDescription = true;
            if (value is null)
            {
                throw new FrontmatterException("description must be a YAML scalar; replace the collection with text.");
            }

            description = string.IsNullOrWhiteSpace(value.Value) ? null : value.Value;
        }

        parser.Consume<DocumentEnd>();
        parser.Consume<StreamEnd>();
        return new(description, null);
    }

    private static Scalar? ReadNode(IParser parser, int depth)
    {
        var node = ReadNodeStart(parser);
        if (node is Scalar scalar)
        {
            return scalar;
        }

        if (depth > MaxNestingDepth)
        {
            throw new FrontmatterException("frontmatter exceeds 32 levels of nesting; simplify the header.");
        }

        if (node is MappingStart)
        {
            while (!parser.TryConsume<MappingEnd>(out _))
            {
                ReadNode(parser, depth + 1);
                ReadNode(parser, depth + 1);
            }
        }
        else
        {
            while (!parser.TryConsume<SequenceEnd>(out _))
            {
                ReadNode(parser, depth + 1);
            }
        }

        return null;
    }

    private static NodeEvent ReadNodeStart(IParser parser)
    {
        if (parser.Accept<AnchorAlias>(out _))
        {
            throw new FrontmatterException("uses YAML anchors or aliases; replace them with literal values.");
        }

        var node = parser.Consume<NodeEvent>();
        if (!node.Anchor.IsEmpty)
        {
            throw new FrontmatterException("uses YAML anchors or aliases; replace them with literal values.");
        }

        if (!node.Tag.IsEmpty)
        {
            throw new FrontmatterException("uses explicit YAML tags; remove the tags from its frontmatter.");
        }

        return node;
    }

    private static SkillDescriptionResult Warn(string reason) =>
        new(null, $"{SkillDiscovery.SkillManifestFileName} {reason}");

    private sealed class FrontmatterException(string message) : Exception(message);
}
