namespace TrayCer.Core.Net;

/// <summary>The printer answered but rejected the credentials (wrong API key or access code).</summary>
public sealed class PrinterAuthException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Something answered, but not the way this kind of printer does (wrong kind or port).</summary>
public sealed class PrinterProtocolException(string message, Exception? inner = null) : Exception(message, inner);
