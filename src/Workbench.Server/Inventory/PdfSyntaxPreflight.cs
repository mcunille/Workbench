// Copyright (c) 2026 The White Stag Collection.

using System.Globalization;
using System.Text;
using UglyToad.PdfPig.Core;

namespace Workbench.Server.Inventory;

// PdfPig normalizes duplicate dictionary keys even in strict mode. Inspect the original
// syntax at its resolved object locations before trusting that normalized object graph.
// Compressed object/xref streams are deliberately unsupported by this preflight.
internal sealed class PdfSyntaxPreflight(byte[] bytes)
{
    private int position;
    private int tokens;

    public void Object(long offset, IndirectReference reference, bool stream)
    {
        Seek(offset);
        if (Unsigned() != reference.ObjectNumber || Unsigned() != reference.Generation || Word() != "obj") Fail();
        Value(0);
        var end = Word();
        if (end != (stream ? "stream" : "endobj")) Fail();
    }

    public void Trailer(long offset)
    {
        Seek(offset);
        if (Word() != "xref") Fail();
        var entries = 0L;
        while (true)
        {
            Skip();
            if (Starts("trailer")) break;
            _ = Unsigned();
            var count = Unsigned();
            entries += count;
            if (entries > 50_001) Fail();
            for (var index = 0L; index < count; index++)
            {
                _ = Unsigned();
                _ = Unsigned();
                var state = Word();
                if (state is not ("n" or "f")) Fail();
            }
        }
        _ = Word();
        Skip();
        if (!Starts("<<")) Fail();
        Value(0);
    }

    private void Value(int depth)
    {
        Skip();
        if (++tokens > 250_000 || depth > 64 || position >= bytes.Length) Fail();
        if (Starts("<<"))
        {
            position += 2;
            var keys = new HashSet<string>(StringComparer.Ordinal);
            while (true)
            {
                Skip();
                if (Starts(">>")) { position += 2; return; }
                if (position >= bytes.Length || bytes[position] != '/') Fail();
                if (!keys.Add(Name())) Fail();
                Value(depth + 1);
            }
        }
        if (bytes[position] == '[')
        {
            position++;
            while (true)
            {
                Skip();
                if (position >= bytes.Length) Fail();
                if (bytes[position] == ']') { position++; return; }
                Value(depth + 1);
            }
        }
        if (bytes[position] == '/') { _ = Name(); return; }
        if (bytes[position] == '(') { Literal(); return; }
        if (bytes[position] == '<')
        {
            position++;
            while (position < bytes.Length && bytes[position] != '>')
            {
                if (!White(bytes[position]) && Hex(bytes[position]) < 0) Fail();
                position++;
            }
            if (position >= bytes.Length) Fail();
            position++;
            return;
        }
        var word = Word();
        if (word is "true" or "false" or "null") return;
        if (word.Length > 32 || word.Any(character => character is not (>= '0' and <= '9' or '+' or '-' or '.'))
            || !double.TryParse(word, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture, out var number) || !double.IsFinite(number)) Fail();
        // An indirect reference is one value, whereas adjacent numbers in an array are separate values.
        var afterNumber = position;
        if (long.TryParse(word, NumberStyles.None, CultureInfo.InvariantCulture, out _))
        {
            Skip();
            if (position < bytes.Length && bytes[position] is >= (byte)'0' and <= (byte)'9')
            {
                var generation = Word();
                if (long.TryParse(generation, NumberStyles.None, CultureInfo.InvariantCulture, out _))
                {
                    Skip();
                    if (Starts("R") && (position + 1 == bytes.Length || Boundary(bytes[position + 1])))
                    { position++; return; }
                }
            }
        }
        position = afterNumber;
    }

    private string Name()
    {
        position++;
        var result = new StringBuilder();
        while (position < bytes.Length && !Boundary(bytes[position]))
        {
            var value = bytes[position++];
            if (value == '#')
            {
                if (position + 1 >= bytes.Length) Fail();
                var first = Hex(bytes[position++]);
                var second = Hex(bytes[position++]);
                if (first < 0 || second < 0) Fail();
                value = (byte)(first * 16 + second);
            }
            if (value == 0) Fail();
            result.Append((char)value);
        }
        return result.ToString();
    }

    private void Literal()
    {
        position++;
        var depth = 1;
        while (position < bytes.Length)
        {
            var value = bytes[position++];
            if (value == '\\')
            {
                if (position >= bytes.Length) Fail();
                // Escaped parentheses do not change nesting. Remaining octal digits are ordinary string bytes.
                position++;
            }
            else if (value == '(')
            {
                if (++depth > 64) Fail();
            }
            else if (value == ')' && --depth == 0) return;
        }
        Fail();
    }

    private long Unsigned()
    {
        if (!long.TryParse(Word(), NumberStyles.None, CultureInfo.InvariantCulture, out var value)) Fail();
        return value;
    }

    private string Word()
    {
        Skip();
        var start = position;
        while (position < bytes.Length && !Boundary(bytes[position])) position++;
        if (position == start) Fail();
        return Encoding.ASCII.GetString(bytes, start, position - start);
    }

    private void Skip()
    {
        while (position < bytes.Length)
        {
            if (White(bytes[position])) { position++; continue; }
            if (bytes[position] != '%') return;
            while (position < bytes.Length && bytes[position] is not (10 or 13)) position++;
        }
    }

    private void Seek(long offset)
    {
        if (offset < 0 || offset >= bytes.Length) Fail();
        position = (int)offset;
    }

    private bool Starts(string value) => bytes.AsSpan(position).StartsWith(Encoding.ASCII.GetBytes(value));
    private static bool White(byte value) => value is 0 or 9 or 10 or 12 or 13 or 32;
    private static bool Boundary(byte value) => White(value) || value is (byte)'(' or (byte)')' or (byte)'<' or (byte)'>'
        or (byte)'[' or (byte)']' or (byte)'{' or (byte)'}' or (byte)'/' or (byte)'%';
    private static int Hex(byte value) => value switch
    {
        >= (byte)'0' and <= (byte)'9' => value - '0',
        >= (byte)'A' and <= (byte)'F' => value - 'A' + 10,
        >= (byte)'a' and <= (byte)'f' => value - 'a' + 10,
        _ => -1
    };
    private static void Fail() => throw new DocumentInputException(422, "The PDF contains ambiguous or unsupported syntax.");
}
