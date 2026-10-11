using System.IO;
using System.Windows.Media;

namespace NPEduTools.App;

// One native playback owner; automation opens the same assets at volume zero.
public sealed class StudyReminderAudio : IDisposable
{
    private readonly MediaPlayer player=new();
    public string? LastCue {get;private set;}
    public int Requests {get;private set;}
    public bool Ready {get;private set;}
    public string? Error {get;private set;}
    public StudyReminderAudio(bool muted)
    {
        player.Volume=muted?0:.65;
        player.MediaOpened+=(_,_)=>Ready=true;
        player.MediaFailed+=(_,e)=>{Error=e.ErrorException.Message;Ready=false;player.Close();};
        player.MediaEnded+=(_,_)=>Stop(); // Release the native decoder between the sparse reminders.
    }
    public void Play(string phase)
    {
        string cue=phase switch {"Preparing" or "Discipline"=>"exam-alert", "Active"=>"exam-start", "Ended"=>"exam-end", _=>throw new ArgumentException("Unknown reminder")};
        player.Stop();player.Close();Ready=false;Error=null;
        LastCue=cue;Requests++;
        player.Open(new Uri(Path.Combine(AppContext.BaseDirectory,"Assets","Study","Audio",cue+".mp3")));
        player.Play();
    }
    public void Stop(){player.Stop();player.Close();Ready=false;}
    public void Dispose()=>Stop();
}
