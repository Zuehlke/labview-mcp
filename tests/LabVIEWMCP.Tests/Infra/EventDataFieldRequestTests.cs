using LabVIEWMcp.Infra;
using Xunit;

namespace LabVIEWMcp.Tests.Infra;

/// <summary>
/// Guards the argument reading of <c>lvai_set_event_data_fields</c>.
///
/// EVERY REFUSAL HERE IS CHEAPER THAN THE ONE IT REPLACES. Without them the mistake surfaces from
/// the helper script, after a bundle has been extracted and the compiled code stripped - or worse,
/// after a rebuild, as a VI that loads and is eBad. The field index in particular has no safe
/// default and no way to be checked later except by exporting the VI and reading the field NAME
/// back, so the description of what the numbers mean has to travel with the refusal.
/// </summary>
public sealed class EventDataFieldRequestTests
{
    [Fact]
    public void ReadsOneNodeAndOneField()
    {
        var reading = EventDataFieldRequests.Read("4244 4280:4");

        Assert.Null(reading.Refusal);
        var request = Assert.Single(reading.Requests);
        Assert.Equal("4244", request.DataNodeUid);
        Assert.Equal(("4280", 4), Assert.Single(request.Takes));
    }

    /// <summary>The script takes the data node's uid first and then one `uid:index` per row, so
    /// this is the shape the whole tool exists to hand over.</summary>
    [Fact]
    public void BuildsTheScriptArgumentsInOrder()
    {
        var reading = EventDataFieldRequests.Read("4244 4280:4 4281:5");

        Assert.Equal(["4244", "4280:4", "4281:5"],
                     Assert.Single(reading.Requests).ScriptArguments);
    }

    /// <summary>One line per Event Data Node - a frame has exactly one, so several frames are
    /// several lines, and each is one run of the helper that can fail on its own.</summary>
    [Fact]
    public void ReadsOneLinePerEventDataNode()
    {
        var reading = EventDataFieldRequests.Read("4244 4280:4\n\n  4255 4281:4  \n");

        Assert.Null(reading.Refusal);
        Assert.Equal(2, reading.Requests.Count);
        Assert.Equal(["4244", "4255"], reading.Requests.Select(r => r.DataNodeUid));
    }

    /// <summary>
    /// THE CEILING IS ARITHMETIC, NOT POLICY: a converted frame has three rows and the Source row
    /// carries no field index to rewrite. Refusing a third here rather than in the script saves an
    /// extract and a strip, and the message has to say WHY two is the limit or it reads as a
    /// tool's arbitrary restriction.
    /// </summary>
    [Fact]
    public void RefusesMoreThanTwoFieldsOnOneNode()
    {
        var reading = EventDataFieldRequests.Read("4244 1:4 2:5 3:6");

        Assert.Contains("ceiling", reading.Refusal);
        Assert.Contains("Source", reading.Refusal);
        Assert.Empty(reading.Requests);
    }

    [Fact]
    public void RefusesALineThatNamesNoField()
    {
        var reading = EventDataFieldRequests.Read("4244");

        Assert.Contains("Line 1", reading.Refusal);
        Assert.Contains("4244 4280:4", reading.Refusal);
    }

    [Fact]
    public void RefusesASpecWithNoColon()
    {
        var reading = EventDataFieldRequests.Read("4244 4280");

        Assert.Contains("'4280'", reading.Refusal);
        Assert.Contains("payload item", reading.Refusal);
    }

    [Fact]
    public void RefusesAFieldIndexThatIsNotANumber()
    {
        var reading = EventDataFieldRequests.Read("4244 4280:Tick");

        Assert.Contains("not a number", reading.Refusal);
    }

    /// <summary>A negative index is not a field. Guarded because `int.TryParse` accepts one and the
    /// script would write it straight into the heap.</summary>
    [Fact]
    public void RefusesANegativeFieldIndex()
    {
        var reading = EventDataFieldRequests.Read("4244 4280:-1");

        Assert.Contains("not a number 0 or above", reading.Refusal);
    }

    /// <summary>The empty call is the one a caller makes first, so its refusal is where the field
    /// numbering gets explained - 4 is the payload, and 3 is the trap.</summary>
    [Fact]
    public void ExplainsTheFieldNumberingWhenNothingWasGiven()
    {
        foreach (var empty in new[] { null, "", "   \n  " })
        {
            var reading = EventDataFieldRequests.Read(empty);
            Assert.Empty(reading.Requests);
            Assert.Contains("UsrEvtRef", reading.Refusal);
            Assert.Contains("4", reading.Refusal);
        }
    }
}
