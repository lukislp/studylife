// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

using StudyLife.Server.Data;
using StudyLife.Shared;

namespace StudyLife.Server.Services.Account;

/// <summary>
/// Writes the "about" block of the personal data export: the information GDPR Art. 15(1)(a)-(h)
/// adds to the copy of the data (2026-11-17 audit F-9). The wording is fixed; only the list of
/// recipients follows what this installation has configured and what the account uses, because
/// that is all the code knows. It names no operator and makes no claim about hosting, backups
/// or logs beyond pointing at the operator.
/// </summary>
public interface IExportAboutBuilder
{
    Task<ExportAboutDto> BuildAsync(AccountExportDto account, CancellationToken ct = default);
}

public sealed class ExportAboutBuilder(
    IAiConsentService aiConsent,
    WebhooksProxyClient webhooks,
    DeveloperProxyClient developers) : IExportAboutBuilder
{
    public async Task<ExportAboutDto> BuildAsync(AccountExportDto account, CancellationToken ct = default)
    {
        var ai = await aiConsent.GetCapabilitiesAsync(ct);
        return new ExportAboutDto
        {
            Purposes = Purposes(ai.Available),
            Categories = ExportAboutText.Categories,
            AddonData = AddonData(account, ai.Available, webhooks.Enabled),
            Recipients = Recipients(account, ai, webhooks.Enabled, developers.Enabled),
            Retention =
            [
                ExportAboutText.RetentionUntilDeleted,
                string.Format(ExportAboutText.RetentionSessions,
                    (int)AuthSessionService.SlidingWindow.TotalDays, (int)AuthSessionService.MaxLifetime.TotalDays),
                ExportAboutText.RetentionOperator,
            ],
            Rights = [ExportAboutText.RightsInApp, ExportAboutText.RightsOperator],
            Complaint = ExportAboutText.Complaint,
            Source = ExportAboutText.Source,
            AutomatedDecisions = ExportAboutText.AutomatedDecisions,
        };
    }

    private static List<string> Purposes(bool aiAvailable)
    {
        var purposes = new List<string>
        {
            ExportAboutText.PurposeService,
            ExportAboutText.PurposeSignIn,
            ExportAboutText.PurposePush,
            ExportAboutText.PurposeIntegrations,
            ExportAboutText.PurposeTelemetry,
        };
        if (aiAvailable) purposes.Add(ExportAboutText.PurposeAi);
        return purposes;
    }

    /// <summary>One line per add-on that holds data of its own about this account. This server cannot
    /// read it, so the export says so instead of implying it is complete.</summary>
    private static List<string> AddonData(AccountExportDto account, bool aiAvailable, bool webhooksEnabled)
    {
        var lines = new List<string>();
        if (aiAvailable) lines.Add(ExportAboutText.AddonDataAi);
        if (webhooksEnabled) lines.Add(ExportAboutText.AddonDataWebhooks);
        if (account.AccessKeys.Count > 0 || account.ConnectedApps.Count > 0)
            lines.Add(ExportAboutText.AddonDataIntegrations);
        return lines;
    }

    private static List<ExportRecipientDto> Recipients(
        AccountExportDto account, AiCapabilities ai, bool webhooksEnabled, bool developersEnabled)
    {
        var recipients = new List<ExportRecipientDto>();

        if (account.PushDevices.Any(d => d.Channel == PushSubscriptionEntity.ChannelWebPush))
            recipients.Add(new() { Name = ExportAboutText.WebPushName, Receives = ExportAboutText.WebPushReceives });
        if (account.PushDevices.Any(d => d.Channel == PushSubscriptionEntity.ChannelApns))
            recipients.Add(new() { Name = ExportAboutText.ApnsName, Receives = ExportAboutText.ApnsReceives });

        if (ai.Available)
        {
            recipients.Add(new() { Name = ExportAboutText.AiAddonName, Receives = ExportAboutText.AiAddonReceives });
            foreach (var role in ai.Provider?.Roles.Where(r => !r.Local) ?? [])
                recipients.Add(new()
                {
                    Name = $"{role.Provider} ({role.Model})",
                    Receives = string.Format(ExportAboutText.AiProviderReceives, role.Role),
                });
            if (ai.Provider is null)
                recipients.Add(new() { Name = ExportAboutText.AiUnknownProviderName, Receives = ExportAboutText.AiUnknownProviderReceives });
        }

        if (webhooksEnabled)
            recipients.Add(new() { Name = ExportAboutText.WebhooksName, Receives = ExportAboutText.WebhooksReceives });
        if (developersEnabled)
            recipients.Add(new() { Name = ExportAboutText.DevelopersName, Receives = ExportAboutText.DevelopersReceives });

        if (account.AccessKeys.Any(k => k.Purpose != AccountExportBuilder.CalendarPurpose))
            recipients.Add(new() { Name = ExportAboutText.KeyHoldersName, Receives = ExportAboutText.KeyHoldersReceives });
        if (account.AccessKeys.Any(k => k.Purpose == AccountExportBuilder.CalendarPurpose))
            recipients.Add(new() { Name = ExportAboutText.CalendarName, Receives = ExportAboutText.CalendarReceives });
        if (account.ConnectedApps.Count > 0)
            recipients.Add(new() { Name = ExportAboutText.ConnectedAppsName, Receives = ExportAboutText.ConnectedAppsReceives });

        return recipients;
    }
}

/// <summary>The fixed English sentences of the "about" block, kept apart from the logic that
/// picks them so they can be read (and reviewed) in one place.</summary>
internal static class ExportAboutText
{
    public const string PurposeService =
        "Run the study planner: store your courses, sessions, notes, goals and settings and keep them in sync across your devices.";
    public const string PurposeSignIn =
        "Sign you in and protect your account: passkeys, password, sign-in sessions and recovery codes.";
    public const string PurposePush =
        "Send the notifications you switched on to the devices you registered for them.";
    public const string PurposeIntegrations =
        "Give the integrations, add-ons and apps you connected the access you granted them, and nothing more.";
    public const string PurposeTelemetry =
        "Usage and error reports, only while you agree to them (see account.telemetryConsent).";
    public const string PurposeAi =
        "AI features of the AI add-on, only after you agreed to them (see account.aiConsent).";

    public const string Categories =
        "This file contains what this server stores about you: your study content in the top-level lists and settings, everything else about your account under \"account\". It does not contain what add-ons of this installation store about you (see \"addonData\").";

    private const string AddonDataWhere =
        " This server cannot read it for this file. Ask the operator of this installation for a copy; deleting your account asks the add-on to delete it.";
    public const string AddonDataAi =
        "AI add-on: the search index built from your notes and your AI conversations." + AddonDataWhere;
    public const string AddonDataWebhooks =
        "Webhooks add-on: the webhook addresses you registered and their delivery log." + AddonDataWhere;
    public const string AddonDataIntegrations =
        "Add-ons for the integrations you connected (for example MCP, voice assistants, chat bots): the links between your account and those services and what they keep for them." + AddonDataWhere;

    public const string WebPushName = "Push service of your browser's vendor (Web Push)";
    public const string WebPushReceives = "Each notification for the browsers listed in account.pushDevices, encrypted so that only the browser can read it.";
    public const string ApnsName = "Apple Push Notification service";
    public const string ApnsReceives = "The text of each notification for the Apple devices listed in account.pushDevices.";
    public const string AiAddonName = "AI add-on of this installation (studylife-ai)";
    public const string AiAddonReceives = "Your notes, courses, sessions and goals, only while your AI consent is in place.";
    public const string AiProviderReceives = "Parts of that content needed for the \"{0}\" task, passed on by the AI add-on.";
    public const string AiUnknownProviderName = "AI provider of the AI add-on (not reported)";
    public const string AiUnknownProviderReceives = "Parts of your content passed on by the AI add-on; ask the operator which provider it uses.";
    public const string WebhooksName = "Webhooks add-on of this installation (studylife-webhooks)";
    public const string WebhooksReceives = "An event for changes to your data, forwarded to the webhook addresses you register there.";
    public const string DevelopersName = "Developer portal of this installation (studylife-developers)";
    public const string DevelopersReceives = "Your developer key and the apps you register, when you use the developer portal.";
    public const string KeyHoldersName = "Integrations you created an access key for (account.accessKeys)";
    public const string KeyHoldersReceives = "The data that key's purpose allows, for example Home Assistant, an MCP client or a browser extension.";
    public const string CalendarName = "Whoever holds your calendar feed link";
    public const string CalendarReceives = "Your planned study sessions as a calendar feed.";
    public const string ConnectedAppsName = "Apps you connected (account.connectedApps)";
    public const string ConnectedAppsReceives = "The data covered by the scopes you granted each app.";

    public const string RetentionUntilDeleted =
        "Your data is kept until you delete it or delete your account. Deleting the account removes it from this server's database and tells the add-ons of this installation to delete theirs.";
    public const string RetentionSessions =
        "A sign-in session ends after {0} days without use, and at the latest {1} days after sign-in.";
    public const string RetentionOperator =
        "Backups and server logs are kept as long as the operator of this installation configured; ask the operator.";

    public const string RightsInApp =
        "You can view and correct your data in the app, download this export at any time, and delete your account in Settings.";
    public const string RightsOperator =
        "For restriction of processing, objection, or any other request, contact the operator of this installation.";
    public const string Complaint =
        "You have the right to lodge a complaint with a data protection supervisory authority, in particular in the EU member state of your habitual residence, place of work or place of the alleged infringement (GDPR Art. 77).";
    public const string Source =
        "You entered this data, it was created by your use of the app, its add-ons and the integrations you connected, or the server created it for your account (for example sign-in sessions). Tags and summaries on captured notes come from the AI add-on.";
    public const string AutomatedDecisions =
        "There is no automated decision-making, including profiling, with legal or similarly significant effects on you (GDPR Art. 22).";
}
