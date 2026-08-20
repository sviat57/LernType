namespace WortBruecke.Core.Abstractions;

public interface IImageProvider
{
    string? Resolve(string relativePath);
}
