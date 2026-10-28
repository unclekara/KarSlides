using KarSlides.App;
using KarSlides.Core.Configuration;
using Microsoft.Extensions.Configuration;

namespace KarSlides.Core.Tests;

public class SettingsTests
{
    private static KarSlidesOptions Bind(params (string Key, string Value)[] values)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value)))
            .Build();
        var o = new KarSlidesOptions();
        Program.BindOptions(config, o);
        return o;
    }

    [Fact]
    public void ConfiguredLists_ReplaceDefaults_NotAppend()
    {
        var o = Bind(("Slots:Monitors:0", "MTT1337"), ("Clicker:Next:0", "PageDown"), ("Clicker:Next:1", "Right"));
        Assert.Equal(["MTT1337"], o.Slots.Monitors);
        Assert.Equal(["PageDown", "Right"], o.Clicker.Next);
    }

    [Fact]
    public void MissingLists_KeepDefaults()
    {
        var o = Bind(("Vmix:HttpPort", "8188"));
        Assert.Equal(["virtual"], o.Slots.Monitors);
        Assert.Equal(["PageDown"], o.Clicker.Next);
        Assert.Equal(["PageDown", "PageUp"], o.Clicker.Swallow);
        Assert.Equal(8188, o.Vmix.HttpPort);
    }
}
