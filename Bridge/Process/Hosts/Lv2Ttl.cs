using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace TuneLab.VstInstruments.Bridge.Hosts;

// A deliberately small Turtle reader for LV2 bundles.
//
// LV2 metadata is RDF/Turtle. Pulling in a full RDF stack for scanning would
// add a native dependency to every helper; instruments only need a handful of
// predicates (plugin type, binary, ports, ranges). This parser implements the
// Turtle subset those bundles use: prefixes, blank nodes, predicate/object
// lists, literals and relative URIs. It does not execute plugin code.
internal static class Lv2Ttl
{
    const string RdfType = "http://www.w3.org/1999/02/22-rdf-syntax-ns#type";
    const string RdfsLabel = "http://www.w3.org/2000/01/rdf-schema#label";
    const string Lv2Plugin = "http://lv2plug.in/ns/lv2core#Plugin";
    const string Lv2InstrumentPlugin = "http://lv2plug.in/ns/lv2core#InstrumentPlugin";
    const string Lv2Binary = "http://lv2plug.in/ns/lv2core#binary";
    const string UiUi = "http://lv2plug.in/ns/extensions/ui#ui";
    const string UiBinary = "http://lv2plug.in/ns/extensions/ui#binary";
    const string Lv2Port = "http://lv2plug.in/ns/lv2core#port";
    const string Lv2Index = "http://lv2plug.in/ns/lv2core#index";
    const string Lv2Symbol = "http://lv2plug.in/ns/lv2core#symbol";
    const string Lv2Name = "http://lv2plug.in/ns/lv2core#name";
    const string Lv2AudioPort = "http://lv2plug.in/ns/lv2core#AudioPort";
    const string Lv2ControlPort = "http://lv2plug.in/ns/lv2core#ControlPort";
    const string Lv2InputPort = "http://lv2plug.in/ns/lv2core#InputPort";
    const string Lv2OutputPort = "http://lv2plug.in/ns/lv2core#OutputPort";
    const string Lv2Minimum = "http://lv2plug.in/ns/lv2core#minimum";
    const string Lv2Maximum = "http://lv2plug.in/ns/lv2core#maximum";
    const string Lv2Default = "http://lv2plug.in/ns/lv2core#default";
    const string Lv2Designation = "http://lv2plug.in/ns/lv2core#designation";
    const string Lv2InstrumentDesignation = "http://lv2plug.in/ns/lv2core#instrument";
    const string AtomPort = "http://lv2plug.in/ns/ext/atom#AtomPort";
    const string AtomSequence = "http://lv2plug.in/ns/ext/atom#Sequence";
    const string AtomSupports = "http://lv2plug.in/ns/ext/atom#supports";
    const string MidiEvent = "http://lv2plug.in/ns/ext/midi#MidiEvent";
    const string TimePosition = "http://lv2plug.in/ns/ext/time#Position";
    const string DoapName = "http://usefulinc.com/ns/doap#name";
    const string DoapMaintainer = "http://usefulinc.com/ns/doap#maintainer";
    const string DoapDeveloper = "http://usefulinc.com/ns/doap#developer";
    const string FoafName = "http://xmlns.com/foaf/0.1/name";
    const string RdfsSeeAlso = "http://www.w3.org/2000/01/rdf-schema#seeAlso";

    public sealed class Term
    {
        public string Value = string.Empty;
        public bool IsBlank;
        public bool IsLiteral;
    }

    public sealed class Triple
    {
        public string Subject = string.Empty;
        public string Predicate = string.Empty;
        public Term Object = new();
    }

    public sealed class Bundle
    {
        public List<Triple> Triples = [];
        public Dictionary<string, string> Prefixes = new(StringComparer.Ordinal);
    }

    public static Bundle ParseBundle(string bundleDirectory)
    {
        var result = new Bundle();

        if (!Directory.Exists(bundleDirectory))
            return result;

        foreach (string file in Directory.EnumerateFiles(bundleDirectory, "*.ttl", SearchOption.AllDirectories))
        {
            string text;

            try
            {
                text = File.ReadAllText(file);
            }
            catch (Exception)
            {
                continue;
            }

            var parsed = Parse(text, Path.GetFileName(file));

            foreach (var pair in parsed.Prefixes)
                result.Prefixes.TryAdd(pair.Key, pair.Value);

            result.Triples.AddRange(parsed.Triples);
        }

        return result;
    }

    public static List<Lv2PluginDescription> Describe(string bundleDirectory)
    {
        var bundle = ParseBundle(bundleDirectory);
        var bySubject = bundle.Triples
            .GroupBy(t => t.Subject, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

        var pluginSubjects = bundle.Triples
            .Where(t => t.Predicate == RdfType
                && !t.Object.IsLiteral
                && (t.Object.Value == Lv2Plugin || t.Object.Value == Lv2InstrumentPlugin))
            .Select(t => t.Subject)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var descriptions = new List<Lv2PluginDescription>();

        foreach (string subject in pluginSubjects)
        {
            if (!bySubject.TryGetValue(subject, out var triples))
                continue;

            var description = DescribePlugin(subject, bundleDirectory, bySubject, triples);

            if (description != null)
                descriptions.Add(description);
        }

        return descriptions;
    }

    static Lv2PluginDescription? DescribePlugin(string subject, string bundleDirectory,
        Dictionary<string, List<Triple>> bySubject, List<Triple> triples)
    {
        bool instrumentType = triples.Any(t => t.Predicate == RdfType
                && !t.Object.IsLiteral && t.Object.Value == Lv2InstrumentPlugin)
            || triples.Any(t => t.Predicate == Lv2Designation
                && !t.Object.IsLiteral
                && (t.Object.Value == Lv2InstrumentDesignation
                    || t.Object.Value.EndsWith("#instrument", StringComparison.Ordinal)));

        string? binary = triples.FirstOrDefault(t => t.Predicate == Lv2Binary)?.Object.Value;
        string? uiUri = triples.FirstOrDefault(t => t.Predicate == UiUi && !t.Object.IsLiteral)?.Object.Value;
        string uiType = string.Empty;
        string uiBinaryPath = string.Empty;

        if (!string.IsNullOrEmpty(uiUri)
            && bySubject.TryGetValue(uiUri, out var uiTriples))
        {
            uiType = uiTriples
                .FirstOrDefault(t => t.Predicate == RdfType && !t.Object.IsLiteral)
                ?.Object.Value ?? string.Empty;

            string? uiBinary = uiTriples
                .FirstOrDefault(t => t.Predicate == Lv2Binary || t.Predicate == UiBinary)
                ?.Object.Value;

            if (!string.IsNullOrWhiteSpace(uiBinary))
            {
                string candidate = ResolveBinary(bundleDirectory, uiBinary);
                if (File.Exists(candidate))
                    uiBinaryPath = candidate;
            }
        }

        if (string.IsNullOrWhiteSpace(binary))
            return null;

        string binaryPath = ResolveBinary(bundleDirectory, binary);

        if (!File.Exists(binaryPath))
            return null;

        var ports = new List<Lv2PortDescription>();

        foreach (var portTerm in triples.Where(t => t.Predicate == Lv2Port))
        {
            if (!bySubject.TryGetValue(portTerm.Object.Value, out var portTriples))
                continue;

            var port = DescribePort(portTerm.Object.Value, portTriples);

            if (port != null)
                ports.Add(port);
        }

        bool hasAudioIn = ports.Any(p => p.IsInput && p.IsAudio);
        bool hasAudioOut = ports.Any(p => p.IsOutput && p.IsAudio);

        if (!hasAudioOut)
            return null;

        // Explicit LV2 instrument metadata wins. Formats/bundles without that
        // marker are classified by signal direction: audio output and no audio
        // input is a generator/instrument, not an effect.
        if (!instrumentType && !(hasAudioOut && !hasAudioIn))
            return null;

        string name = Literal(triples, DoapName)
            ?? Literal(triples, RdfsLabel)
            ?? Path.GetFileNameWithoutExtension(bundleDirectory);

        string vendor = Vendor(triples, bySubject);

        return new Lv2PluginDescription
        {
            Uri = subject,
            Name = name,
            Vendor = vendor,
            BinaryPath = binaryPath,
            UiUri = uiUri ?? string.Empty,
            UiType = uiType,
            UiBinaryPath = uiBinaryPath,
            BundlePath = bundleDirectory,
            Ports = ports,
            IsInstrument = true,
        };
    }

    static Lv2PortDescription? DescribePort(string subject, List<Triple> triples)
    {
        var types = triples.Where(t => t.Predicate == RdfType && !t.Object.IsLiteral)
            .Select(t => t.Object.Value)
            .ToHashSet(StringComparer.Ordinal);

        if (!types.Contains(Lv2AudioPort) && !types.Contains(Lv2ControlPort) && !types.Contains(AtomPort))
            return null;

        int? index = IntLiteral(triples, Lv2Index);

        if (index == null)
            return null;

        return new Lv2PortDescription
        {
            Index = index.Value,
            Name = Literal(triples, Lv2Name) ?? Literal(triples, Lv2Symbol) ?? subject,
            Symbol = Literal(triples, Lv2Symbol) ?? string.Empty,
            IsAudio = types.Contains(Lv2AudioPort),
            IsControl = types.Contains(Lv2ControlPort),
            IsAtom = types.Contains(AtomPort),
            IsInput = types.Contains(Lv2InputPort),
            IsOutput = types.Contains(Lv2OutputPort),
            SupportsMidi = types.Contains(AtomPort) && triples.Any(t =>
                t.Predicate == AtomSupports && !t.Object.IsLiteral && t.Object.Value == MidiEvent),
            SupportsTime = types.Contains(AtomPort) && triples.Any(t =>
                t.Predicate == AtomSupports && !t.Object.IsLiteral && t.Object.Value == TimePosition),
            BufferType = triples.FirstOrDefault(t => t.Predicate.EndsWith("#bufferType", StringComparison.Ordinal))?.Object.Value,
            Minimum = DoubleLiteral(triples, Lv2Minimum) ?? 0,
            Maximum = DoubleLiteral(triples, Lv2Maximum) ?? 1,
            Default = DoubleLiteral(triples, Lv2Default) ?? 0,
        };
    }

    static string? Literal(List<Triple> triples, string predicate)
        => triples.FirstOrDefault(t => t.Predicate == predicate && t.Object.IsLiteral)?.Object.Value;

    static int? IntLiteral(List<Triple> triples, string predicate)
    {
        string? text = Literal(triples, predicate);

        return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
            ? value
            : null;
    }

    static double? DoubleLiteral(List<Triple> triples, string predicate)
    {
        string? text = Literal(triples, predicate);

        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
            ? value
            : null;
    }

    static string Vendor(List<Triple> triples, Dictionary<string, List<Triple>> bySubject)
    {
        foreach (string predicate in new[] { DoapMaintainer, DoapDeveloper })
        {
            var term = triples.FirstOrDefault(t => t.Predicate == predicate)?.Object;

            if (term == null)
                continue;

            if (term.IsLiteral)
                return term.Value;

            if (bySubject.TryGetValue(term.Value, out var subjectTriples))
            {
                string? name = Literal(subjectTriples, FoafName) ?? Literal(subjectTriples, DoapName);

                if (!string.IsNullOrWhiteSpace(name))
                    return name;
            }
        }

        return string.Empty;
    }

    static string ResolveBinary(string bundleDirectory, string binary)
    {
        string value = binary;

        if (value.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
            value = new Uri(value).LocalPath;
        else if (value.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            value = new Uri(value).LocalPath;

        if (Path.IsPathRooted(value))
            return value;

        return Path.GetFullPath(Path.Combine(bundleDirectory, value));
    }

    public static Bundle Parse(string text, string source = "")
    {
        var tokens = Tokenize(text);
        var prefixes = new Dictionary<string, string>(StringComparer.Ordinal);
        var triples = new List<Triple>();
        int nextBlank = 0;
        int position = 0;

        string NewBlank() => "_:b" + (++nextBlank).ToString(CultureInfo.InvariantCulture) + ":" + source;

        while (position < tokens.Count)
        {
            if (tokens[position] == "@prefix" || tokens[position].Equals("PREFIX", StringComparison.OrdinalIgnoreCase))
            {
                position++;

                if (position + 1 >= tokens.Count)
                    break;

                string prefix = tokens[position].TrimEnd(':');
                position++;
                string value = Unwrap(tokens[position]);
                position++;

                if (position < tokens.Count && tokens[position] == ".")
                    position++;

                prefixes[prefix] = value;
                continue;
            }

            if (tokens[position] == "@base" || tokens[position].Equals("BASE", StringComparison.OrdinalIgnoreCase))
            {
                position += 2;

                if (position < tokens.Count && tokens[position] == ".")
                    position++;

                continue;
            }

            if (tokens[position] == ".")
            {
                position++;
                continue;
            }

            Term subject = ParseNode(ref position, tokens, prefixes, triples, NewBlank);

            if (subject.IsLiteral)
            {
                position++;
                continue;
            }

            ParsePredicateObjectList(subject.Value, ref position, tokens, prefixes, triples, NewBlank);

            if (position < tokens.Count && tokens[position] == ".")
                position++;
        }

        return new Bundle { Triples = triples, Prefixes = prefixes };
    }

    static Term ParseNode(ref int position, List<string> tokens,
        Dictionary<string, string> prefixes, List<Triple> triples, Func<string> newBlank)
    {
        if (position >= tokens.Count)
            return new Term();

        if (tokens[position] == "[")
        {
            position++;
            string blank = newBlank();
            ParsePredicateObjectList(blank, ref position, tokens, prefixes, triples, newBlank);

            if (position < tokens.Count && tokens[position] == "]")
                position++;

            return new Term { Value = blank, IsBlank = true };
        }

        if (tokens[position] == "(")
        {
            // RDF collection: not needed for plugin/port metadata; skip to the
            // matching parenthesis so the surrounding document still parses.
            int depth = 0;

            while (position < tokens.Count)
            {
                if (tokens[position] == "(")
                    depth++;
                else if (tokens[position] == ")" && --depth == 0)
                {
                    position++;
                    break;
                }

                position++;
            }

            return new Term { Value = newBlank(), IsBlank = true };
        }

        string token = tokens[position++];
        bool literal = token.StartsWith('"') || LooksLikeLiteral(token);

        return new Term
        {
            Value = literal ? DecodeLiteral(token) : Expand(token, prefixes),
            IsBlank = token.StartsWith("_:"),
            IsLiteral = literal,
        };
    }

    static void ParsePredicateObjectList(string subject, ref int position, List<string> tokens,
        Dictionary<string, string> prefixes, List<Triple> triples, Func<string> newBlank)
    {
        while (position < tokens.Count)
        {
            if (tokens[position] is "." or "]" or "}")
                return;

            if (tokens[position] == ";")
            {
                position++;
                continue;
            }

            string predicateToken = tokens[position++];

            if (predicateToken == "a")
                predicateToken = RdfType;
            else
                predicateToken = Expand(predicateToken, prefixes);

            while (position < tokens.Count)
            {
                Term objectTerm = ParseNode(ref position, tokens, prefixes, triples, newBlank);

                triples.Add(new Triple
                {
                    Subject = subject,
                    Predicate = predicateToken,
                    Object = objectTerm,
                });

                if (position < tokens.Count && tokens[position] == ",")
                {
                    position++;
                    continue;
                }

                break;
            }

            if (position < tokens.Count && tokens[position] == ".")
                return;
        }
    }

    static string Expand(string token, Dictionary<string, string> prefixes)
    {
        if (token.StartsWith('<') && token.EndsWith('>'))
            return Unwrap(token);

        int colon = token.IndexOf(':');

        if (colon > 0)
        {
            string prefix = token[..colon];

            if (prefixes.TryGetValue(prefix, out string? value))
                return value + token[(colon + 1)..];
        }

        return token;
    }

    static string Unwrap(string token)
        => token.Length >= 2 && token[0] == '<' && token[^1] == '>'
            ? token[1..^1]
            : token;

    static bool LooksLikeLiteral(string token)
    {
        if (token.Length == 0)
            return false;

        char first = token[0];

        return char.IsDigit(first) || first is '-' or '+' or '.'
            || token is "true" or "false";
    }

    static int FindClosingQuote(string text, int start)
    {
        for (int i = start; i < text.Length; i++)
        {
            if (text[i] == '\\')
            {
                i++;
                continue;
            }

            if (text[i] == '"')
                return i;
        }

        return -1;
    }

    static string DecodeLiteral(string token)
    {
        string value = token;

        if (value.StartsWith("\"\"\"", StringComparison.Ordinal))
        {
            int end = value.IndexOf("\"\"\"", 3, StringComparison.Ordinal);

            if (end >= 3)
                value = value[3..end];
        }
        else if (value.Length >= 2 && value[0] == '"')
        {
            int end = FindClosingQuote(value, 1);

            if (end > 0)
                value = value[1..end];
        }

        return value
            .Replace("\\\"", "\"")
            .Replace("\\n", "\n")
            .Replace("\\r", "\r")
            .Replace("\\t", "\t")
            .Replace("\\\\", "\\");
    }

    static List<string> Tokenize(string text)
    {
        var tokens = new List<string>();
        int i = 0;

        while (i < text.Length)
        {
            char c = text[i];

            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }

            if (c == '#')
            {
                while (i < text.Length && text[i] != '\n')
                    i++;

                continue;
            }

            if (c == '<')
            {
                int start = i++;

                while (i < text.Length && text[i] != '>')
                    i++;

                if (i < text.Length)
                    i++;

                tokens.Add(text[start..i]);
                continue;
            }

            if (c == '"')
            {
                int start = i++;

                if (i + 1 < text.Length && text[i] == '"' && text[i + 1] == '"')
                {
                    i += 2;

                    while (i + 2 < text.Length
                        && !(text[i] == '"' && text[i + 1] == '"' && text[i + 2] == '"'))
                        i++;

                    i = Math.Min(text.Length, i + 3);
                }
                else
                {
                    while (i < text.Length)
                    {
                        if (text[i] == '\\')
                        {
                            i += 2;
                            continue;
                        }

                        if (text[i] == '"')
                        {
                            i++;
                            break;
                        }

                        i++;
                    }
                }

                // A literal may carry a language tag or datatype suffix; keep it in the
                // token so the reader can ignore it without derailing the parse.
                while (i < text.Length
                    && !char.IsWhiteSpace(text[i])
                    && text[i] is not ';' and not ',' and not ']' and not ')')
                {
                    i++;
                }

                tokens.Add(text[start..Math.Min(i, text.Length)]);
                continue;
            }

            if (c is '[' or ']' or '(' or ')' or ';' or ',')
            {
                tokens.Add(c.ToString());
                i++;
                continue;
            }

            if (c == '.')
            {
                // A dot is a terminator unless it is part of a number.
                if (i + 1 < text.Length && char.IsDigit(text[i + 1]))
                {
                    int start = i++;

                    while (i < text.Length && (char.IsDigit(text[i]) || text[i] == '.'))
                        i++;

                    tokens.Add(text[start..i]);
                }
                else
                {
                    tokens.Add(".");
                    i++;
                }

                continue;
            }

            int wordStart = i;

            while (i < text.Length
                && !char.IsWhiteSpace(text[i])
                && text[i] is not '<' and not '"' and not '[' and not ']' and not '(' and not ')' and not ';' and not ',')
            {
                if (text[i] == '.' &&
                    (i + 1 >= text.Length || char.IsWhiteSpace(text[i + 1]) || text[i + 1] is ';' or ',' or ']'))
                {
                    break;
                }

                i++;
            }

            if (i == wordStart)
                i++;

            tokens.Add(text[wordStart..i]);
        }

        return tokens;
    }
}

internal sealed class Lv2PluginDescription
{
    public string Uri = string.Empty;
    public string Name = string.Empty;
    public string Vendor = string.Empty;
    public string BinaryPath = string.Empty;
    public string UiUri = string.Empty;
    public string UiType = string.Empty;
    public string UiBinaryPath = string.Empty;
    public string BundlePath = string.Empty;
    public bool IsInstrument;
    public List<Lv2PortDescription> Ports = [];
}

internal sealed class Lv2PortDescription
{
    public int Index;
    public string Name = string.Empty;
    public string Symbol = string.Empty;
    public bool IsAudio;
    public bool IsControl;
    public bool IsAtom;
    public bool IsInput;
    public bool IsOutput;
    public bool SupportsMidi;
    public bool SupportsTime;
    public string? BufferType;
    public double Minimum;
    public double Maximum;
    public double Default;
}