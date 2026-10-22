using DotnetPackageSkills.NuGet;

namespace DotnetPackageSkills.Tests;

public class PackageCoordinateTests
{
    [Theory]
    [InlineData("Mockly@1.10.0", "Mockly", "1.10.0")]
    [InlineData("Contoso.Widgets@2.3.0", "Contoso.Widgets", "2.3.0")]
    [InlineData("My_Package-Name@1.0.0-beta.1", "My_Package-Name", "1.0.0-beta.1")]
    [InlineData("_Acme@1.0.0", "_Acme", "1.0.0")]
    [InlineData("Acme_@1.0.0", "Acme_", "1.0.0")]
    [InlineData("Widgets@2.0", "Widgets", "2.0")]
    [InlineData("Widgets@1", "Widgets", "1")]
    [InlineData("Widgets@1.2.3.4", "Widgets", "1.2.3.4")]
    [InlineData("Widgets@01.02.003.0-BETA.1+build.001", "Widgets", "01.02.003.0-BETA.1+build.001")]
    [InlineData("Widgets@1.2.3+sha.abc", "Widgets", "1.2.3+sha.abc")]
    [InlineData("  Mockly@1.10.0  ", "Mockly", "1.10.0")]
    [InlineData("Contoso.Überlib@1.0.0", "Contoso.Überlib", "1.0.0")]
    public void Parse_accepts_an_exact_package_and_version(string input, string id, string version)
    {
        var coordinate = PackageCoordinate.Parse(input);

        Assert.Equal(id, coordinate.Id);
        Assert.Equal(version, coordinate.Version);
    }

    [Theory]
    [InlineData("Mockly@4.*")]
    [InlineData("Mockly@*")]
    [InlineData("Mockly@1.2.*")]
    [InlineData("Mockly@[1.0,2.0)")]
    [InlineData("Mockly@(,3.0]")]
    [InlineData("Mockly@[1.0]")]
    public void Parse_refuses_floating_versions_and_ranges(string input)
    {
        var exception = Assert.Throws<PackageSkillsException>(() => PackageCoordinate.Parse(input));

        // Guessing a version would copy skills describing a release the user does not use,
        // so the message has to point at the option that resolves versions properly.
        Assert.Contains("exact version", exception.Message);
        Assert.Contains("--target", exception.Message);
    }

    [Theory]
    [InlineData("Mockly", "Mockly")]
    [InlineData("  mOcKlY  ", "mOcKlY")]
    [InlineData("_Acme", "_Acme")]
    [InlineData("Contoso.Überlib", "Contoso.Überlib")]
    public void Parse_accepts_a_bare_id_for_manifest_or_target_version_resolution(string input, string id)
    {
        var coordinate = PackageCoordinate.Parse(input);

        Assert.Equal(id, coordinate.Id);
        Assert.Null(coordinate.Version);
        Assert.Equal(id, coordinate.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("@1.0.0")]
    [InlineData("Mockly@")]
    [InlineData("Mockly@@1.0.0")]
    [InlineData("Mockly@not-a-version")]
    [InlineData("Mockly@v1.0.0")]
    [InlineData("Mockly@1.0.0-alpha.")]
    [InlineData("Mockly@1.0.0+a..b")]
    [InlineData("Mockly@1.0.0-")]
    [InlineData("Mockly@1.0.0+")]
    [InlineData("Mockly@1.0.0-a..b")]
    [InlineData("Mockly@1.0.0-.alpha")]
    [InlineData("Mockly@1.0.0+a.")]
    [InlineData("Mockly@1.0.0-beta.01")]
    [InlineData("Mockly@1.2.3.4.5")]
    [InlineData("Mockly@2147483648.0.0")]
    [InlineData("Mockly@1.0.0.2147483648")]
    [InlineData("../evil@1.0.0")]
    [InlineData("path/to/thing@1.0.0")]
    [InlineData("Contoso..Widgets@1.0.0")]
    [InlineData("Contoso.-Widgets@1.0.0")]
    [InlineData("Contoso\u200B.Widgets@1.0.0")]
    [InlineData("../evil")]
    [InlineData("path/to/thing")]
    [InlineData("Contoso..Widgets")]
    [InlineData("Mockly*")]
    [InlineData("not a package")]
    public void Parse_rejects_malformed_input(string input) =>
        Assert.Throws<PackageSkillsException>(() => PackageCoordinate.Parse(input));

    [Theory]
    [InlineData('A', 100, true)]
    [InlineData('A', 101, false)]
    [InlineData('\u00DC', 100, true)]
    [InlineData('\u00DC', 101, false)]
    public void Package_id_length_follows_NuGet_packaging_rules(
        char character, int length, bool valid)
    {
        Assert.Equal(valid, PackageCoordinate.IsValidId(new string(character, length)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Maximum_length_ids_are_accepted_in_bare_and_exact_selectors(bool exact)
    {
        var id = new string('A', 100);
        var coordinate = PackageCoordinate.Parse(exact ? $"{id}@1.0.0" : id);

        Assert.Equal(id, coordinate.Id);
        Assert.Equal(exact ? "1.0.0" : null, coordinate.Version);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Overlength_ids_are_rejected_in_bare_and_exact_selectors(bool exact)
    {
        var id = new string('A', 101);
        var error = Assert.Throws<PackageSkillsException>(
            () => PackageCoordinate.Parse(exact ? $"{id}@1.0.0" : id));

        Assert.Contains("100", error.Message);
    }

    [Fact]
    public void ToString_round_trips() =>
        Assert.Equal("Mockly@1.10.0", PackageCoordinate.Parse("Mockly@1.10.0").ToString());

    [Fact]
    public void Display_retains_the_trimmed_original_coordinate_spelling() =>
        Assert.Equal("Mockly@01.10-BETA.1+Build.01",
            PackageCoordinate.Parse("  Mockly  @  01.10-BETA.1+Build.01  ").ToString());
}
