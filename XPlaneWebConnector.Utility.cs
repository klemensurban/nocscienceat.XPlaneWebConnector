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



}