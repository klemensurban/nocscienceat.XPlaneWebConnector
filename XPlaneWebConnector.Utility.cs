using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace nocscienceat.XPlaneWebConnector;

public sealed partial class XPlaneWebConnector
{
    /// <summary>
    /// Regex to extract array index from dataRef paths like "AirbusFBW/Foo[7]".
    /// </summary>
    [GeneratedRegex(@"^(.+)\[(\d+)\]$")]
    private static partial Regex ArrayIndexRegex();

    /// <summary>
    /// Dataref path parsing: "AirbusFBW/Foo[7]" ? ("AirbusFBW/Foo", 7)
    /// Parses a dataref path and extracts the base path and array index if present.
    /// Extracts array indices from paths like "AirbusFBW/Foo[7]" into the base path and index components.
    /// If no array index is found, returns the full path with index -1.
    /// </summary>
    /// <param name="path">The dataref path potentially containing an array index notation.</param>
    /// <returns>A tuple containing the base path and array index (or -1 if no index is present).</returns>
    private static (string BasePath, int Index) ParseDataRefPath(string path)
    {
        var match = ArrayIndexRegex().Match(path);
        if (match.Success)
            return (match.Groups[1].Value, int.Parse(match.Groups[2].Value));
        return (path, -1);
    }


    /// <summary>
    /// Converts a JSON array of numeric elements into a UTF-8 decoded string.
    /// </summary>
    /// <remarks>
    /// This method treats each numeric element in the JSON array as a byte value,
    /// reassembles them into a byte array, and decodes the result as UTF-8 text.
    /// Automatically trims trailing null bytes (0x00) before decoding.
    /// If no null terminator is found, the entire byte array is decoded.
    /// </remarks>
    /// <param name="arrayElement">A JsonElement representing a JSON array where each element is a numeric byte value.</param>
    /// <returns>A UTF-8 decoded string with trailing null bytes removed.</returns>
    private static string DecodeByteArrayToString(JsonElement arrayElement)
    {
        // data-type arrays: each element is a byte ? reassemble and decode
        var bytes = new byte[arrayElement.GetArrayLength()];
        int i = 0;
        foreach (var el in arrayElement.EnumerateArray())
        {
            bytes[i++] = (byte)el.GetInt32();
        }
        // Trim trailing nulls
        int len = Array.IndexOf(bytes, (byte)0);
        return Encoding.UTF8.GetString(bytes, 0, len >= 0 ? len : bytes.Length);
    }
}