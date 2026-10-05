namespace MeetVault.Core;

public enum Gender { Male, Female, Unknown }

// ponytail: counter dict only; per-speaker persistence lives in repository (P3).
public sealed class SpeakerLabeler
{
    private readonly Dictionary<Gender, int> _counters = new() { [Gender.Male] = 0, [Gender.Female] = 0 };

    public string Assign(Gender gender) => gender switch
    {
        Gender.Male => $"Male {++_counters[Gender.Male]}",
        Gender.Female => $"Female {++_counters[Gender.Female]}",
        _ => "Unknown",
    };
}
