namespace Rakit.Core;

public static class ExceptionMessages
{
    /// <summary>
    /// <c>ArgumentException.Message</c> ends with " (Parameter 'args')"; that is noise on a screen.
    /// Other exceptions are returned unchanged.
    /// </summary>
    public static string WithoutParameterSuffix(Exception ex) =>
        ex is ArgumentException { ParamName: { Length: > 0 } name }
            ? ex.Message.Replace($" (Parameter '{name}')", "")
            : ex.Message;
}
