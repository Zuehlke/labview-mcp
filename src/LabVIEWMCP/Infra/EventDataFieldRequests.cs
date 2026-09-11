namespace LabVIEWMcp.Infra;

/// <summary>
/// What <c>lvai_set_event_data_fields</c> was asked to repoint, read off the caller's text.
///
/// ONE LINE PER EVENT DATA NODE, because that is one run of the helper script and therefore one
/// unit that can fail on its own. A frame has exactly one data node, so a line is also "one frame".
///
/// THE UIDS ARE THE ONES THE AUTHOR WROTE IN THE AIXML, never heap numbers. For a node the two are
/// the same; for a diagram CONSTANT they are not - LabVIEW keeps the authored uid on the <c>ddo</c>
/// inside a <c>term</c> that gets a fresh number, measured 2026-09-11 with a constant authored as
/// 4280 arriving as term 4598. The script resolves both, so nothing here has to know the difference
/// and no caller has to read a heap.
/// </summary>
internal static class EventDataFieldRequests
{
    /// <param name="DataNodeUid">The Event Data Node's uid.</param>
    /// <param name="Takes">Placeholder uid and the event-data field index to show, in order. The
    /// rows are consumed in the order LabVIEW made them, so the order here is the order on the
    /// node.</param>
    internal sealed record Request(string DataNodeUid, List<(string Uid, int Field)> Takes)
    {
        /// <summary>The arguments the helper script takes after the bundle and base name.</summary>
        internal string[] ScriptArguments =>
            [DataNodeUid, .. Takes.Select(t => $"{t.Uid}:{t.Field}")];
    }

    internal sealed record Reading(List<Request> Requests, string? Refusal);

    /// <summary>
    /// THE CEILING IS TWO FIELDS PER NODE, and it is arithmetic rather than policy: a converted
    /// frame has three rows, and the one showing <c>Source</c> carries no field index to rewrite.
    /// The script refuses a third as well - this refuses it before a bundle is extracted, which is
    /// the cheaper of the two places to find out.
    /// </summary>
    private const int RowsPerNode = 2;

    internal static Reading Read(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return new Reading([], """
                No fields given. Pass one line per Event Data Node:
                  <dataNodeUid> <placeholderUid>:<fieldIndex> [<placeholderUid>:<fieldIndex>]
                for example `4244 4280:4`. The uids are the ones YOUR AIXML wrote - the data node's
                and the placeholder constant's - and the field index is 4 for the first payload
                item (0 Source, 1 Type, 2 Time, 3 UsrEvtRef).
                """);

        var requests = new List<Request>();
        var lineNumber = 0;
        foreach (var raw in text.Split('\n'))
        {
            lineNumber++;
            var line = raw.Trim();
            if (line.Length == 0)
                continue;

            var tokens = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length < 2)
                return new Reading([], $"""
                    Line {lineNumber} is '{line}', which names {tokens.Length} thing(s). A line is
                    the data node's uid followed by at least one `<placeholderUid>:<fieldIndex>`,
                    for example `4244 4280:4`.
                    """);

            var takes = new List<(string, int)>();
            foreach (var spec in tokens.Skip(1))
            {
                var colon = spec.LastIndexOf(':');
                if (colon <= 0 || colon == spec.Length - 1)
                    return new Reading([], $"""
                        Line {lineNumber} has '{spec}', which is not `<placeholderUid>:<fieldIndex>`.
                        The index says WHICH event-data field the row should show: 4 is the first
                        payload item, and 0/1/2/3 are Source, Type, Time and UsrEvtRef.
                        """);

                if (!int.TryParse(spec[(colon + 1)..], out var field) || field < 0)
                    return new Reading([], $"""
                        Line {lineNumber} has '{spec}'.
                        Its field index is not a number 0 or above, and it has to be one: the index
                        says which event-data field the row shows, counting 0 Source, 1 Type,
                        2 Time, 3 UsrEvtRef, 4 the first payload item.
                        """);

                takes.Add((spec[..colon], field));
            }

            if (takes.Count > RowsPerNode)
                return new Reading([], $"""
                    Line {lineNumber} asks for {takes.Count} fields on one Event Data Node, and
                    {RowsPerNode} is the ceiling. A converted frame has three rows - Source, Type
                    and Time - and the Source row carries no field index to rewrite, so only two can
                    be repointed. Read the rest off the payload cluster downstream, or split the
                    work over more frames.
                    """);

            requests.Add(new Request(tokens[0], takes));
        }

        return requests.Count == 0
            ? new Reading([], "Every line was blank, so there is nothing to repoint.")
            : new Reading(requests, null);
    }
}
