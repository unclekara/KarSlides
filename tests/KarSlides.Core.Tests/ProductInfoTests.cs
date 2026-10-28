using System.Text.Json;
using KarSlides.Control;
using KarSlides.Core.Slots;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;

namespace KarSlides.Core.Tests;

public sealed class ProductInfoTests
{
    [Fact]
    public void Current_HasSemVer_ShortCommit_AndAuthor()
    {
        var p = ProductInfo.Current;

        Assert.Matches(@"^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$", p.Version);
        Assert.True(p.Commit.Length is 0 or 7, p.Commit);
        Assert.Equal("Aleksandr Karabatov", p.Author);
        Assert.Equal("hellokarabatov@gmail.com", p.Email);
        Assert.StartsWith(p.Version, p.Display);
    }

    [Fact]
    public async Task State_ReportsProduct()
    {
        await using var app = ControlServer.Create(new Configuration.ControlOptions(), new SlotSet([]), new AppStatus(),
            b => b.WebHost.UseTestServer());
        await app.StartAsync();

        var json = JsonDocument.Parse(await app.GetTestClient().GetStringAsync("/api/state")).RootElement;

        Assert.Equal(ProductInfo.Current.Version, json.GetProperty("product").GetProperty("version").GetString());
        Assert.Equal(ProductInfo.Current.Display, json.GetProperty("product").GetProperty("display").GetString());
    }
}
