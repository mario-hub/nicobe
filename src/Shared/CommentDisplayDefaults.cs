namespace NicoBE;

// Build-time source of truth for player and ASS encoder display defaults.
// Both projects compile this file; runtime user settings remain independent.
public static class CommentDisplayDefaults
{
    public const string FontFamily = "Yu Gothic UI";
    public const int FontWeight = 700;
    public const double FontSize = 48;
    public const double FontSizePercent = 100;
    public const double ScrollPixelsPerSecond = 360;
    public const double ScrollSpeedPercent = 100;
    public const double MinScrollSpeedPercent = 50;
    public const double LineSpacingPercent = 30;
    public const int OutlineLevel = 1;
    public const int ShadowLevel = 2;
    public const bool AccelerateLongComments = true;
    public const bool ColorEmoji = true;
    public const double FixedCommentSeconds = 5;
    public const int MaxVisibleComments = 100;
    public const double Opacity = .90;
    public const double ShadowOpacityScale = .65;
    public const double AdvanceSeconds = 0;
    public const int AccelerationCharacterLimit = 50;
    public const double AccelerationPerCharacter = .02;
    public static double OutlineWidth(int level) => level switch {0=>0,1=>1.5,2=>2.5,3=>4,4=>6,5=>8,_=>1.5};
    public static double ShadowOffset(int level) => level switch {0=>0,1=>1,2=>2,3=>3,4=>4,5=>6,_=>2};
}
