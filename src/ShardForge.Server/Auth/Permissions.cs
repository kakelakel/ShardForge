namespace ShardForge.Server.Auth;

/// <summary>What each level may do: shown on the Admin page. The server enforces it at every endpoint.</summary>
public static class Permissions
{
    public sealed record Row(string What, Role Least, string? Note = null);

    public static readonly Row[] Table =
    [
        new("Open maps and projects, walk around, see the team", Role.Viewer),
        new("Place statics, houses and decorations", Role.Decorator),
        new("Place doors, signs, working items and spawners", Role.Decorator),
        new("Save designs and pieces to the house library", Role.Decorator),
        new("Remove or change what you placed yourself", Role.Decorator, "Statics, server items and library designs"),
        new("Remove or change anything, whoever placed it", Role.Worldbuilder),
        new("Edit the land (paint, raise, lower, smooth)", Role.Worldbuilder),
        new("Place regions and teleporters", Role.Worldbuilder),
        new("Create, edit, save and build projects in the editor", Role.Worldbuilder),
        new("Export and download map files, ServUO packages and the activity log", Role.Admin),
        new("Publish maps to the server's disk", Role.Admin),
        new("Delete projects", Role.Admin),
        new("Manage people, settings and the client folder; read the activity log", Role.Admin),
    ];
}
