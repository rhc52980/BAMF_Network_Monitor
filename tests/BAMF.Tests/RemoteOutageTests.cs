using LanWatch.Services;

namespace BAMF.Tests;

/// <summary>When another BAMF server is called down: after it has worked and then missed three in a row, said once, and said again when it's back.</summary>
public class RemoteOutageTests
{
    private static readonly DateTime T = new(2026, 10, 4, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void A_server_that_worked_is_called_down_on_the_third_miss_and_only_then()
    {
        var o = new RemoteService.Outage();
        Assert.Null(RemoteService.Step(o, true, T));
        Assert.Null(RemoteService.Step(o, false, T.AddMinutes(1)));
        Assert.Null(RemoteService.Step(o, false, T.AddMinutes(2)));
        Assert.Equal("down", RemoteService.Step(o, false, T.AddMinutes(3)));
        Assert.Null(RemoteService.Step(o, false, T.AddMinutes(4)));      // already said
        Assert.Equal(T.AddMinutes(1), o.Since);
    }

    [Fact]
    public void When_it_answers_again_that_is_said_once_and_only_if_the_down_was()
    {
        var o = new RemoteService.Outage();
        RemoteService.Step(o, true, T);
        RemoteService.Step(o, false, T.AddMinutes(1));
        Assert.Null(RemoteService.Step(o, true, T.AddMinutes(2)));       // a blip: no down was said, so no back
        Assert.Equal(0, o.Failures);
        for (var m = 3; m <= 5; m++) RemoteService.Step(o, false, T.AddMinutes(m));
        Assert.Equal("back", RemoteService.Step(o, true, T.AddMinutes(6)));
        Assert.Null(RemoteService.Step(o, true, T.AddMinutes(7)));
        Assert.Null(o.Since);
    }

    [Fact]
    public void A_server_that_has_never_answered_is_a_wrong_address_not_an_outage()
    {
        var o = new RemoteService.Outage();
        for (var m = 0; m < 10; m++) Assert.Null(RemoteService.Step(o, false, T.AddMinutes(m)));
    }

    [Fact]
    public void A_second_outage_is_said_again()
    {
        var o = new RemoteService.Outage();
        RemoteService.Step(o, true, T);
        for (var m = 1; m <= 3; m++) RemoteService.Step(o, false, T.AddMinutes(m));
        RemoteService.Step(o, true, T.AddMinutes(4));
        RemoteService.Step(o, false, T.AddMinutes(5));
        RemoteService.Step(o, false, T.AddMinutes(6));
        Assert.Equal("down", RemoteService.Step(o, false, T.AddMinutes(7)));
    }
}
