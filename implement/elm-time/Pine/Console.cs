﻿using System.Collections.Generic;
using System.Collections.Immutable;

namespace Pine;

public interface IConsole
{
    void WriteLine(string text, TextColor color = TextColor.Default) =>
        Write("\n" + text, color);

    void Write(string text, TextColor color) =>
        Write(ImmutableList.Create((text, color)));

    void Write(IReadOnlyList<(string text, TextColor color)> coloredTexts);

    public record struct ColoredText(string text, TextColor color);

    public enum TextColor
    {
        Default,
        Green,
        Red
    }
}

public class StaticConsole : IConsole
{
    static readonly public System.ConsoleColor InitialForegroundColor = System.Console.ForegroundColor;

    static readonly public StaticConsole Instance = new();

    public void Write(IReadOnlyList<(string text, IConsole.TextColor color)> coloredTexts)
    {
        foreach (var (text, color) in coloredTexts)
        {
            System.Console.ForegroundColor =
                color switch
                {
                    IConsole.TextColor.Default => InitialForegroundColor,
                    IConsole.TextColor.Green => System.ConsoleColor.Green,
                    IConsole.TextColor.Red => System.ConsoleColor.Red,
                    _ => throw new System.NotImplementedException()
                };

            System.Console.Write(text);
        }

        System.Console.ForegroundColor = InitialForegroundColor;
    }
}