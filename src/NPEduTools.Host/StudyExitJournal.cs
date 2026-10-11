using System.Security.Cryptography;
using System.Text.Json;
using NPEduTools.Contracts;
using NPEduTools.Integrations.Npep;
namespace NPEduTools.Host;
public sealed class StudyExitJournal(string root, Func<byte[],bool,byte[]> protect)
{
    private readonly object _sync=new();
    private readonly string _root=Path.GetFullPath(root);
    private IEnumerable<string> Files()=>Directory.Exists(_root)?Directory.EnumerateFiles(_root,"*.exit").Where(f=>Guid.TryParseExact(Path.GetFileNameWithoutExtension(f),"N",out _)).Take(128):[];
    public void Save(StudyExitPacket packet)
    {
        lock(_sync) {
            if(Files().Count()>=128)throw new IOException("EXIT_QUEUE_FULL");
            NpepPhotoProtocol.Validate("exitMetadata",packet.Metadata);
            byte[] clear=JsonSerializer.SerializeToUtf8Bytes(packet,Protocol.Json),encrypted;
            try {encrypted=protect(clear,true);}finally {CryptographicOperations.ZeroMemory(clear);}
            if(encrypted.Length>8192)throw new InvalidDataException();
            Directory.CreateDirectory(_root);string path=Path.Combine(_root,Guid.Parse(packet.Metadata["eventId"]!.GetValue<string>()).ToString("N")+".exit"),temporary=path+"."+Guid.NewGuid().ToString("N")+".tmp";
            try {using(var file=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None,4096,FileOptions.WriteThrough)){file.Write(encrypted);file.Flush(true);}File.Move(temporary,path,true);}
            finally {if(File.Exists(temporary))File.Delete(temporary);}
        }
    }
    public StudyExitPacket? Next(string scope)
    {
        lock(_sync)foreach(string file in Files().OrderBy(File.GetCreationTimeUtc)) {
            if(new FileInfo(file).Length>8192)throw new InvalidDataException();
            byte[] clear=protect(File.ReadAllBytes(file),false);StudyExitPacket packet;
            try{packet=JsonSerializer.Deserialize<StudyExitPacket>(clear,Protocol.Json)??throw new InvalidDataException();}finally{CryptographicOperations.ZeroMemory(clear);}
            NpepPhotoProtocol.Validate("exitMetadata",packet.Metadata);
            if(packet.Scope!=scope){File.Delete(file);continue;}return packet;
        }
        return null;
    }
    public void Remove(Guid id){lock(_sync)File.Delete(Path.Combine(_root,id.ToString("N")+".exit"));}
    public void Clear(){lock(_sync)foreach(string file in Files())File.Delete(file);}
}
