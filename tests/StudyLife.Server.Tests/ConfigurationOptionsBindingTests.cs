using Microsoft.Extensions.Options;
using StudyLife.Server.Configuration;

namespace StudyLife.Server.Tests;

/// <summary>
/// Protects the typed-options layer itself: the options classes replaced ~37 raw
/// `_config["Section:Key"]` reads, so the KEY NAMES and the DEFAULTS are now declared in exactly
/// one place and nothing else in the app would notice a typo in them. Two representative sections
/// are pinned here - Worker (scalars with non-trivial defaults, plus the one validated value) and
/// Consent (a bool plus the per-audience dictionary that replaced a dynamically built
/// configuration key). Both go through the production registration
/// (StudyLifeOptionsRegistration), not a hand-built instance.
/// </summary>
public class ConfigurationOptionsBindingTests
{
    [Fact]
    public void WorkerSection_BindsEveryKeyItDocuments()
    {
        var worker = TestOptions.For<WorkerOptions>(
            ("Worker:Enabled", "false"),
            ("Worker:ReplicaCount", "3"),
            ("Worker:ReplicaCountSource", "Kubernetes"),
            ("Worker:DeploymentName", "studylife-worker-canary")).Value;

        Assert.False(worker.Enabled);
        Assert.Equal(3, worker.ReplicaCount);
        Assert.Equal("Kubernetes", worker.ReplicaCountSource);
        Assert.Equal("studylife-worker-canary", worker.DeploymentName);
        Assert.True(worker.UsesKubernetesReplicaCount);
    }

    [Fact]
    public void WorkerSection_Absent_KeepsTheDocumentedSingleContainerDefaults()
    {
        var worker = TestOptions.For<WorkerOptions>().Value;

        Assert.True(worker.Enabled);
        Assert.Equal(1, worker.ReplicaCount);
        Assert.Equal("Static", worker.ReplicaCountSource);
        Assert.Equal("studylife-worker", worker.DeploymentName);
        Assert.False(worker.UsesKubernetesReplicaCount);
    }

    /// <summary>Individual keys are independent: setting one must not reset the others to their
    /// C# defaults, which is the failure mode a hand-written options class invites.</summary>
    [Fact]
    public void WorkerSection_PartiallyConfigured_LeavesTheOtherKeysAtTheirDefaults()
    {
        var worker = TestOptions.For<WorkerOptions>(("Worker:Enabled", "false")).Value;

        Assert.False(worker.Enabled);
        Assert.Equal(1, worker.ReplicaCount);
        Assert.Equal("studylife-worker", worker.DeploymentName);
    }

    /// <summary>The one value validated at startup (ValidateDataAnnotations/ValidateOnStart):
    /// "id % ReplicaCount" was never workable below 1.</summary>
    [Fact]
    public void WorkerReplicaCount_BelowOne_IsRejected()
    {
        var options = TestOptions.For<WorkerOptions>(("Worker:ReplicaCount", "0"));

        Assert.Throws<OptionsValidationException>(() => options.Value);
    }

    [Fact]
    public void ConsentSection_BindsRequirePkceAndThePerAudienceRedirectUris()
    {
        var consent = TestOptions.For<ConsentOptions>(
            ("Consent:RequirePkce", "true"),
            ("Consent:AllowedRedirectUris:mcp:0", "https://mcp.example.com/auth/studylife/callback"),
            ("Consent:AllowedRedirectUris:mcp:1", "https://mcp2.example.com/cb"),
            ("Consent:AllowedRedirectUris:tray:0", "https://tray.example.com/cb")).Value;

        Assert.True(consent.RequirePkce);
        Assert.Equal(
            ["https://mcp.example.com/auth/studylife/callback", "https://mcp2.example.com/cb"],
            consent.AllowedRedirectUris["mcp"]);
        Assert.Equal(["https://tray.example.com/cb"], consent.AllowedRedirectUris["tray"]);
        Assert.False(consent.AllowedRedirectUris.ContainsKey("capture"));
    }

    [Fact]
    public void ConsentSection_Absent_LeavesPkceOptionalAndNoConfiguredRedirectUris()
    {
        var consent = TestOptions.For<ConsentOptions>().Value;

        Assert.False(consent.RequirePkce);
        Assert.Empty(consent.AllowedRedirectUris);
    }
}
