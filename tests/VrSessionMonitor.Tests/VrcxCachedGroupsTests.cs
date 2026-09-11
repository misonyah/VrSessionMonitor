using VrSessionMonitor.Modules;
using Xunit;

namespace VrSessionMonitor.Tests;

/// <summary>
/// Covers parsing of VRCX's own cached group list (configs key
/// config:vrcx_currentusergroups_&lt;user id&gt;), which is what the Automation tab's Group
/// dropdown is built from. Shape confirmed against a real VRCX database: a JSON array of
/// objects carrying id, name, iconUrl, ownerId, roleIds and roles - notably NO shortCode,
/// unlike the API's users/{id}/groups.
/// </summary>
public class VrcxCachedGroupsTests
{
    [Fact]
    public void Parses_id_and_name_from_the_cached_shape()
    {
        var json = """
        [
          {"id":"grp_11111111-1111-1111-1111-111111111111","name":"First Group","iconUrl":"https://example/i.png","ownerId":"usr_x","roleIds":[],"roles":[]},
          {"id":"grp_22222222-2222-2222-2222-222222222222","name":"Second Group","ownerId":"usr_y"}
        ]
        """;
        var groups = VrcxSessionProvider.ParseCachedGroups(json);

        Assert.Equal(2, groups.Count);
        Assert.Equal("grp_11111111-1111-1111-1111-111111111111", groups[0].Id);
        Assert.Equal("First Group", groups[0].Name);
        // The cache carries no short code; that field is simply absent rather than invented.
        Assert.Null(groups[0].ShortCode);
    }

    [Fact]
    public void Skips_entries_missing_an_id_or_a_name()
    {
        // A blank-named entry would otherwise become an unpickable blank row in the dropdown.
        var json = """
        [
          {"id":"grp_1","name":"Keep me"},
          {"id":"grp_2"},
          {"name":"No id here"},
          {"id":"grp_3","name":"   "},
          {"id":"grp_4","name":"Keep me too"}
        ]
        """;
        var groups = VrcxSessionProvider.ParseCachedGroups(json);

        Assert.Equal(2, groups.Count);
        Assert.Equal(new[] { "Keep me", "Keep me too" }, groups.Select(g => g.Name).ToArray());
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json at all")]
    [InlineData("{\"id\":\"grp_1\"}")]   // object, not the expected array
    [InlineData("[1, 2, 3]")]            // array of non-objects
    public void Malformed_input_yields_empty_rather_than_throwing(string json)
    {
        Assert.Empty(VrcxSessionProvider.ParseCachedGroups(json));
    }

    [Fact]
    public void Missing_database_yields_empty()
    {
        var provider = new VrcxSessionProvider(Path.Combine(Path.GetTempPath(), "no_such_vrcx_" + Path.GetRandomFileName() + ".sqlite3"));
        Assert.Empty(provider.TryLoadCachedGroups());
    }
}
