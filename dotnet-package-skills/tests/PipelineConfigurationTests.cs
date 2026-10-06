using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using SharpYaml;
using SharpYaml.Events;

namespace DotnetPackageSkills.Tests;

public class PipelineConfigurationTests
{
    [Fact]
    public void Public_plan_excludes_production_resources_even_with_internal_source_variables()
    {
        var plan = ExpandPlan(official: false, TrustedVariables());

        Assert.Equal("/eng/common/templates/jobs/jobs.yml", plan.Template);
        Assert.Equal("True", Text(plan.Parameters["runAsPublic"]));
        Assert.Equal("False", Text(plan.Parameters["enableMicrobuild"]));
        Assert.Equal("False", Text(plan.Parameters["enablePublishBuildAssets"]));
        Assert.Equal("False", Text(plan.Parameters["enablePublishing"]));
        Assert.DoesNotContain("_SignType", plan.JobText);
        Assert.DoesNotContain("_TeamName", plan.JobText);
        Assert.DoesNotContain("SigningTeamName", plan.JobText);
        Assert.DoesNotContain("templateContext", plan.Job.Keys.Cast<string>());
        Assert.Contains("PublishPipelineArtifact@1", plan.JobText);
    }

    [Fact]
    public void Trusted_official_plan_uses_Arcade_MicroBuild_and_governed_success_only_packages()
    {
        var plan = ExpandPlan(official: true, TrustedVariables());

        Assert.Equal("/eng/common/templates-official/jobs/jobs.yml@self", plan.Template);
        Assert.Equal("False", Text(plan.Parameters["runAsPublic"]));
        Assert.Equal("True", Text(plan.Parameters["enableMicrobuild"]));
        Assert.Equal("False", Text(plan.Parameters["enableMicrobuildForMacAndLinux"]));
        Assert.Contains("_SignType", plan.JobText);
        Assert.Contains("real", plan.JobText);
        Assert.Contains("Validate trusted source", plan.JobText);
        Assert.DoesNotContain("PublishPipelineArtifact@1", plan.JobText);
        var outputs = List(Map(plan.Job["templateContext"])["outputs"]).Select(Map).ToArray();
        Assert.Equal(3, outputs.Length);
        var packages = Assert.Single(outputs, output => Text(output["artifactName"]) == "dotnet-package-skills-packages");
        Assert.Contains("succeeded()", Text(packages["condition"]));
        Assert.Contains("DotnetPackageSkillsPackageVerified", Text(packages["condition"]));
        Assert.All(outputs.Where(output => !ReferenceEquals(output, packages)),
            output => Assert.Equal("False", Text(output["isProduction"])));
    }

    [Theory]
    [InlineData("Build.Repository.Provider", "GitHub")]
    [InlineData("Build.Repository.Name", "NuGet/Client.Tools")]
    [InlineData("System.TeamProject", "public")]
    [InlineData("System.CollectionUri", "https://dev.azure.com/dnceng-public/")]
    [InlineData("Build.SourceBranch", "refs/heads/feature")]
    [InlineData("Build.Reason", "PullRequest")]
    [InlineData("Build.Reason", "")]
    public void Untrusted_official_plans_reject_before_including_signing_resources(string name, string value)
    {
        var variables = TrustedVariables();
        variables[name] = value;

        var plan = ExpandPlan(official: true, variables);

        Assert.Equal("True", Text(plan.Parameters["runAsPublic"]));
        Assert.Equal("False", Text(plan.Parameters["enableMicrobuild"]));
        Assert.Equal("RejectUntrustedOfficialSource", Text(plan.Job["job"]));
        Assert.DoesNotContain("_SignType", plan.JobText);
        Assert.DoesNotContain("_TeamName", plan.JobText);
        Assert.DoesNotContain("SigningTeamName", plan.JobText);
        Assert.DoesNotContain("Invoke-Build.ps1", plan.JobText);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Expanded_steps_use_one_root_graph_and_never_pack_after_signing(bool official)
    {
        var plan = ExpandPlan(official, TrustedVariables());
        var steps = List(plan.Job["steps"]).Select(Map).ToArray();
        var scripts = steps.Where(step => step.ContainsKey("pwsh")).Select(step => Text(step["pwsh"])).ToArray();
        Assert.Equal(new[] { "RestoreBuild", "Test", "Pack", "Sign", "Verify" },
            scripts.Select(script => Regex.Match(script, @"-Action (\w+)").Groups[1].Value).Where(value => value != ""));
        Assert.All(steps.Where(step => step.ContainsKey("workingDirectory")),
            step => Assert.Equal("$(Build.SourcesDirectory)", Text(step["workingDirectory"])));
        var runtimes = steps.Where(step => step.TryGetValue("task", out var task) && Text(task) == "UseDotNet@2").ToArray();
        Assert.Equal(2, runtimes.Length);
        Assert.All(runtimes, step => Assert.Equal("$(DOTNET_INSTALL_DIR)", Text(Map(step["inputs"])["installationPath"])));
        var publish = Assert.Single(steps, step => step.TryGetValue("task", out var task) && Text(task) == "PublishTestResults@2");
        Assert.Equal("True", Text(Map(publish["inputs"])["failTaskOnFailedTests"]));
        Assert.Equal("True", Text(Map(publish["inputs"])["failTaskOnMissingResultsFile"]));
        Assert.Equal("True", Text(Map(publish["inputs"])["failTaskOnFailureToPublishResults"]));
    }

    [Fact]
    public void Root_entry_points_preserve_main_only_triggers_and_1ES_governance()
    {
        var publicRoot = Read(Path.Combine(PipelineTestContext.RepoRoot, "eng", "pipelines", "pr.yml"));
        Assert.Equal(new[] { "main" }, List(Map(Map(publicRoot["pr"])["branches"])["include"]).Select(Text));
        Assert.False(publicRoot.ContainsKey("resources"));
        Assert.Single(List(publicRoot["stages"]));

        var officialRoot = Read(Path.Combine(PipelineTestContext.RepoRoot, "eng", "pipelines", "official.yml"));
        Assert.Equal("$(Date:yyyyMMdd).$(Rev:r)", Text(officialRoot["name"]));
        Assert.Equal(new[] { "main" }, List(officialRoot["trigger"]).Select(Text));
        Assert.Equal("v1/1ES.Official.PipelineTemplate.yml@1esPipelines", Text(Map(officialRoot["extends"])["template"]));
        Assert.Single(List(Map(Map(officialRoot["extends"])["parameters"])["stages"]));
        var release = Assert.Single(List(officialRoot["parameters"]).Select(Map));
        Assert.Equal("DotnetPackageSkillsReleaseBuild", Text(release["name"]));
        Assert.Equal("False", Text(release["default"]));
    }

    [Fact]
    public void Steps_forwarded_through_Arcade_jobs_use_a_root_absolute_template_reference()
    {
        var root = Read(PipelineTestContext.PipelinePath("jobs-build.yml"));
        var wrapper = Map(Assert.Single(List(root["jobs"])));
        var job = Map(Assert.Single(List(Map(wrapper["parameters"])["jobs"])));
        var step = Map(Assert.Single(List(job["steps"])));

        Assert.Equal("/eng/pipelines/dotnet-package-skills/steps-build.yml", Text(step["template"]));
    }

    private static Dictionary<string, string> TrustedVariables() => new()
    {
        ["Build.Repository.Provider"] = "TfsGit",
        ["Build.Repository.Name"] = "NuGet-Client.Tools",
        ["System.TeamProject"] = "internal",
        ["System.CollectionUri"] = "https://dev.azure.com/dnceng/",
        ["Build.SourceBranch"] = "refs/heads/main",
        ["Build.Reason"] = "Manual",
    };

    private static ExpandedPlan ExpandPlan(bool official, Dictionary<string, string> variables)
    {
        var root = ExpandOwned("stage.yml", new() { ["isOfficialBuild"] = official }, variables);
        var stage = Map(Assert.Single(List(root["stages"])));
        Assert.Equal("DotnetPackageSkills", Text(stage["stage"]));
        var template = Map(Assert.Single(List(stage["jobs"])));
        if (Text(template["template"]) == "jobs-build.yml")
        {
            var jobs = ExpandOwned("jobs-build.yml", Map(template["parameters"]), variables);
            template = Map(Assert.Single(List(jobs["jobs"])));
        }
        var parameters = Map(template["parameters"]);
        var job = Map(Assert.Single(List(parameters["jobs"])));
        var steps = new List<object>();
        foreach (var step in List(job["steps"]).Select(Map))
        {
            if (step.TryGetValue("template", out var path) && Text(path) == "/eng/pipelines/dotnet-package-skills/steps-build.yml")
            {
                steps.AddRange(List(ExpandOwned("steps-build.yml", Map(step["parameters"]), variables)["steps"]));
            }
            else { steps.Add(step); }
        }
        job["steps"] = steps;
        return new(Text(template["template"]), parameters, job);
    }

    // Expand owned templates only. Server-side public expansion and the authorized 1ES run are separate acceptance checks.
    private static Dictionary<object, object> ExpandOwned(string file, Dictionary<object, object> overrides,
        Dictionary<string, string> variables)
    {
        var root = Read(PipelineTestContext.PipelinePath(file));
        var parameters = List(root["parameters"]).Select(Map).ToDictionary(parameter => Text(parameter["name"]), parameter => parameter["default"]);
        foreach (var (key, value) in overrides) { parameters[Text(key)] = value; }
        return Map(Expand(root, parameters, variables));
    }

    private static object Expand(object node, Dictionary<string, object> parameters, Dictionary<string, string> variables)
    {
        if (node is string text)
        {
            if (text.StartsWith("${{ ", StringComparison.Ordinal) && text.EndsWith(" }}", StringComparison.Ordinal) &&
                text.IndexOf("${{ ", 4, StringComparison.Ordinal) < 0)
            {
                return Evaluate(text[4..^3], parameters, variables);
            }
            return Regex.Replace(text, @"\$\{\{ (.*?) \}\}", match => Text(Evaluate(match.Groups[1].Value, parameters, variables)));
        }
        if (node is Dictionary<object, object> mapping)
        {
            var expanded = new Dictionary<object, object>();
            var matched = false;
            foreach (var (key, value) in mapping)
            {
                if (Text(key).StartsWith("${{ if ", StringComparison.Ordinal))
                {
                    matched = Convert.ToBoolean(Evaluate(Text(key)[7..^3], parameters, variables), CultureInfo.InvariantCulture);
                    if (matched)
                    {
                        foreach (var (nestedKey, nestedValue) in Map(Expand(value, parameters, variables))) { expanded.Add(nestedKey, nestedValue); }
                    }
                }
                else if (Text(key) == "${{ else }}")
                {
                    if (!matched)
                    {
                        foreach (var (nestedKey, nestedValue) in Map(Expand(value, parameters, variables))) { expanded.Add(nestedKey, nestedValue); }
                    }
                }
                else { expanded.Add(key, Expand(value, parameters, variables)); }
            }
            return expanded;
        }
        if (node is List<object> sequence)
        {
            var expanded = new List<object>();
            var matched = false;
            foreach (var item in sequence)
            {
                if (item is Dictionary<object, object> conditional && conditional.Count == 1 &&
                    Text(conditional.Keys.Single()).StartsWith("${{ if ", StringComparison.Ordinal))
                {
                    matched = Convert.ToBoolean(Evaluate(Text(conditional.Keys.Single())[7..^3], parameters, variables), CultureInfo.InvariantCulture);
                    if (matched) { expanded.AddRange(List(Expand(conditional.Values.Single(), parameters, variables))); }
                }
                else if (item is Dictionary<object, object> alternative && alternative.Count == 1 && alternative.ContainsKey("${{ else }}"))
                {
                    if (!matched) { expanded.AddRange(List(Expand(alternative["${{ else }}"], parameters, variables))); }
                }
                else { expanded.Add(Expand(item, parameters, variables)); }
            }
            return expanded;
        }
        return node;
    }

    private static object Evaluate(string expression, Dictionary<string, object> parameters, Dictionary<string, string> variables)
    {
        expression = expression.Trim();
        if (bool.TryParse(expression, out var boolean)) { return boolean; }
        if (expression.StartsWith('\'') && expression.EndsWith('\'')) { return expression[1..^1]; }
        if (expression.StartsWith("parameters.", StringComparison.Ordinal)) { return parameters[expression[11..]]; }
        if (expression.StartsWith("variables['", StringComparison.Ordinal)) { return variables.GetValueOrDefault(expression[11..^2], ""); }
        var opening = expression.IndexOf('(');
        if (opening < 0 || !expression.EndsWith(')')) { throw new InvalidOperationException($"Unsupported template expression: {expression}"); }
        var arguments = SplitArguments(expression[(opening + 1)..^1]).Select(argument => Evaluate(argument, parameters, variables)).ToArray();
        return expression[..opening] switch
        {
            "eq" => string.Equals(Text(arguments[0]), Text(arguments[1]), StringComparison.OrdinalIgnoreCase),
            "ne" => !string.Equals(Text(arguments[0]), Text(arguments[1]), StringComparison.OrdinalIgnoreCase),
            "and" => arguments.All(argument => Convert.ToBoolean(argument, CultureInfo.InvariantCulture)),
            "not" => !Convert.ToBoolean(arguments[0], CultureInfo.InvariantCulture),
            _ => throw new InvalidOperationException($"Unsupported template expression: {expression}"),
        };
    }

    private static IEnumerable<string> SplitArguments(string expression)
    {
        var start = 0;
        var depth = 0;
        var quoted = false;
        for (var index = 0; index < expression.Length; index++)
        {
            var character = expression[index];
            if (character == '\'') { quoted = !quoted; }
            if (quoted) { continue; }
            if (character == '(') { depth++; }
            if (character == ')') { depth--; }
            if (character == ',' && depth == 0) { yield return expression[start..index]; start = index + 1; }
        }
        yield return expression[start..];
    }

    private static Dictionary<object, object> Read(string path)
    {
        var reader = new EventReader(Parser.CreateParser(new StringReader(File.ReadAllText(path))));
        reader.Expect<StreamStart>();
        reader.Expect<DocumentStart>();
        var mapping = Map(ReadNode(reader));
        reader.Expect<DocumentEnd>();
        reader.Expect<StreamEnd>();
        return mapping;
    }

    private static object ReadNode(EventReader reader)
    {
        if (reader.Allow<Scalar>() is { } scalar)
        {
            if (scalar.Style == ScalarStyle.Plain && bool.TryParse(scalar.Value, out var boolean)) { return boolean; }
            return scalar.Value;
        }
        if (reader.Allow<MappingStart>() is not null)
        {
            var mapping = new Dictionary<object, object>();
            while (reader.Allow<MappingEnd>() is null) { mapping.Add(ReadNode(reader), ReadNode(reader)); }
            return mapping;
        }
        if (reader.Allow<SequenceStart>() is not null)
        {
            var sequence = new List<object>();
            while (reader.Allow<SequenceEnd>() is null) { sequence.Add(ReadNode(reader)); }
            return sequence;
        }
        throw new InvalidOperationException("Unsupported YAML node in pipeline configuration.");
    }
    private static Dictionary<object, object> Map(object value) => Assert.IsType<Dictionary<object, object>>(value);
    private static List<object> List(object value) => Assert.IsType<List<object>>(value);
    private static string Text(object value) => Convert.ToString(value, CultureInfo.InvariantCulture) ?? throw new InvalidOperationException("Missing YAML value.");
    private sealed record ExpandedPlan(string Template, Dictionary<object, object> Parameters, Dictionary<object, object> Job)
    {
        internal string JobText => JsonSerializer.Serialize(Job);
    }
}
