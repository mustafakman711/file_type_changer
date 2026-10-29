namespace Nashi.Core;

public enum DragPhase{Idle,Pressed,Dragging,Opened,Ignored}

/// The global drag gesture, independent of how input is observed. A press
/// arms nothing; only crossing the 10 px threshold with the primary button
/// held starts modifier checks, and the release always ends them. Nothing is
/// evaluated while Idle, so an idle Nashi does no periodic work.
public sealed class DragGesture
{
    public const int Threshold=10;
    public DragPhase Phase{get;private set;}
    public int StartX{get;private set;}
    public int StartY{get;private set;}
    /// True while modifier chords should be evaluated.
    public bool Armed=>Phase==DragPhase.Dragging;
    /// True from the press until the release.
    public bool Tracking=>Phase!=DragPhase.Idle;

    public void Press(int x,int y){Phase=DragPhase.Pressed;StartX=x;StartY=y;}

    /// Returns true exactly once, when the drag crosses the threshold with the
    /// primary button: the caller then starts checking modifiers.
    public bool Move(int x,int y,bool primaryHeld)
    {
        if(Phase!=DragPhase.Pressed||Math.Abs(x-StartX)+Math.Abs(y-StartY)<Threshold)return false;
        Phase=primaryHeld?DragPhase.Dragging:DragPhase.Ignored;return Phase==DragPhase.Dragging;
    }

    /// The wheel kind for the held chord, opening it at most once per drag.
    public string? Evaluate(string chord,Preferences preferences)
    {
        if(Phase!=DragPhase.Dragging)return null;
        var kind=Kind(chord,preferences);if(kind is not null)Phase=DragPhase.Opened;return kind;
    }

    /// Ends the drag. Returns true when a wheel was opened and must be dismissed.
    public bool Release(){var opened=Phase==DragPhase.Opened;Phase=DragPhase.Idle;return opened;}

    public static string? Kind(string chord,Preferences p)=>chord==p.ToolsModifier?"tools":chord==p.ConvertModifier?"convert":chord==p.ActionsModifier?"actions":null;
    public static string Chord(bool ctrl,bool shift,bool alt)
    {
        var parts=new List<string>(3);if(ctrl)parts.Add("Ctrl");if(shift)parts.Add("Shift");if(alt)parts.Add("Alt");return string.Join('+',parts);
    }
}
