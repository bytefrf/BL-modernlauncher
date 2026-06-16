namespace Launcher.App;

public sealed record ErrorInfo(
    string Title,
    string Summary,
    IReadOnlyList<string> Actions,
    string TechnicalDetails);
