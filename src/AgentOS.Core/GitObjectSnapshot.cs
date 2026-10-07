using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
namespace AgentOS.Core;
public sealed record GitTreeEntry(string Path,string Mode,string Type,string Sha);
public sealed record GitSnapshotObject(string Sha,string Type,string? Base64,GitTreeEntry[]? Entries);
public sealed record GitCommitSnapshot(string Sha,string Tree,string[] Parents,string Message,string AuthorName,string AuthorEmail,string AuthorDate,string CommitterName,string CommitterEmail,string CommitterDate);
public sealed record GitObjectSnapshot(GitCommitSnapshot Commit,GitSnapshotObject[] Objects)
{
 public const int MaxObjects=256,MaxBytes=3*1024*1024;
 static string Hash(string type,byte[] data)=>Convert.ToHexString(SHA1.HashData(Encoding.ASCII.GetBytes(type+" "+data.Length+"\0").Concat(data).ToArray())).ToLowerInvariant();
 public static async Task<GitObjectSnapshot> CaptureAsync(string directory,string tip,CancellationToken ct)
 {
  if(!Regex.IsMatch(tip,"^[0-9a-f]{40}$"))throw new InvalidDataException("Git SHA-1 required.");
  var bytes=0;var objects=new List<GitSnapshotObject>();var seen=new HashSet<string>();
  async Task<byte[]> Read(string type,string sha)
  {
   ct.ThrowIfCancellationRequested();var info=new ProcessStartInfo("git"){WorkingDirectory=directory,UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
   foreach(var arg in new[]{"cat-file",type,sha})info.ArgumentList.Add(arg);
   info.Environment["GIT_TERMINAL_PROMPT"]="0";info.Environment["GIT_CONFIG_NOSYSTEM"]="1";info.Environment["GIT_CONFIG_GLOBAL"]="NUL";
   using var p=Process.Start(info)??throw new InvalidOperationException("Cannot start Git.");var errors=p.StandardError.ReadToEndAsync(ct);
   using var output=new MemoryStream();var buffer=new byte[8192];int n;
   try{while((n=await p.StandardOutput.BaseStream.ReadAsync(buffer,ct))!=0){if(bytes+output.Length+n>MaxBytes){p.Kill(true);throw new InvalidDataException("Git snapshot byte limit.");}output.Write(buffer,0,n);}await p.WaitForExitAsync(ct);}
   catch(OperationCanceledException){if(!p.HasExited)p.Kill(true);throw;}
   _=await errors;if(p.ExitCode!=0)throw new InvalidDataException("Git object unavailable.");var data=output.ToArray();bytes+=data.Length;if(Hash(type,data)!=sha)throw new InvalidDataException("Git object SHA mismatch.");return data;
  }
  async Task Walk(string type,string sha)
  {
   if(!seen.Add(sha))return;if(seen.Count>MaxObjects)throw new InvalidDataException("Git snapshot object limit.");var data=await Read(type,sha);
   if(type=="blob"){objects.Add(new(sha,type,Convert.ToBase64String(data),null));return;}
   var entries=new List<GitTreeEntry>();int i=0;
   while(i<data.Length){int start=i;while(i<data.Length&&data[i]!=(byte)' ')i++;if(i==data.Length)throw new InvalidDataException("Invalid tree.");var mode=Encoding.ASCII.GetString(data,start,i++-start);start=i;
    while(i<data.Length&&data[i]!=0)i++;if(i==data.Length||i-start>255||i+21>data.Length)throw new InvalidDataException("Invalid tree entry.");
    var name=new UTF8Encoding(false,true).GetString(data,start,i++-start);var child=Convert.ToHexString(data.AsSpan(i,20)).ToLowerInvariant();i+=20;
    var childType=mode=="40000"?"tree":mode is "100644" or "100755" or "120000"?"blob":throw new InvalidDataException("Unsupported tree mode.");await Walk(childType,child);entries.Add(new(name,mode,childType,child));}
   objects.Add(new(sha,"tree",null,entries.ToArray()));
  }
  var raw=await Read("commit",tip);var source=new UTF8Encoding(false,true).GetString(raw);var split=source.IndexOf("\n\n",StringComparison.Ordinal);if(split<0)throw new InvalidDataException("Invalid commit.");
  var lines=source[..split].Split('\n');if(lines.Length<4||!lines[0].StartsWith("tree ")||lines.Any(x=>!(x.StartsWith("tree ")||x.StartsWith("parent ")||x.StartsWith("author ")||x.StartsWith("committer "))))throw new InvalidDataException("Unsupported signed or encoded commit.");
  var tree=lines[0][5..];var parents=lines.Where(x=>x.StartsWith("parent ")).Select(x=>x[7..]).ToArray();if(parents.Length!=1||!Regex.IsMatch(tree,"^[0-9a-f]{40}$")||parents.Any(p=>!Regex.IsMatch(p,"^[0-9a-f]{40}$")))throw new InvalidDataException("Unsupported commit references.");
  static (string Name,string Email,string Date) Person(string line){var m=Regex.Match(line,@"^(.{1,200}) <([^<>\r\n]{1,200})> ([0-9]{1,12}) ([+-][0-9]{4})$");if(!m.Success)throw new InvalidDataException("Unsupported commit identity.");var offset=m.Groups[4].Value;var minutes=int.Parse(offset[1..3])*60+int.Parse(offset[3..]);if(offset[0]=='-')minutes=-minutes;return(m.Groups[1].Value,m.Groups[2].Value,DateTimeOffset.FromUnixTimeSeconds(long.Parse(m.Groups[3].Value)).ToOffset(TimeSpan.FromMinutes(minutes)).ToString("yyyy-MM-ddTHH:mm:sszzz",CultureInfo.InvariantCulture));}
  var a=Person(lines.Single(x=>x.StartsWith("author "))[7..]);var c=Person(lines.Single(x=>x.StartsWith("committer "))[10..]);await Walk("tree",tree);
  var result=new GitObjectSnapshot(new(tip,tree,parents,source[(split+2)..],a.Name,a.Email,a.Date,c.Name,c.Email,c.Date),objects.ToArray());result.Validate(tip);return result;
 }
 public void Validate(string expectedTip)
 {
  if(Commit.Sha!=expectedTip||Commit.Parents.Length!=1||Commit.Parents.Any(p=>!Regex.IsMatch(p,"^[0-9a-f]{40}$"))||!Regex.IsMatch(Commit.Tree,"^[0-9a-f]{40}$")||Objects.Length is <1 or >MaxObjects||Objects[^1].Sha!=Commit.Tree||Objects.Sum(x=>x.Base64?.Length??0)>MaxBytes*2)throw new InvalidDataException("Git snapshot scope mismatch.");
  static string Identity(string name,string email,string date){var d=DateTimeOffset.ParseExact(date,"yyyy-MM-ddTHH:mm:sszzz",CultureInfo.InvariantCulture);return name+" <"+email+"> "+d.ToUnixTimeSeconds()+" "+d.ToString("zzz").Replace(":","");}
  var raw="tree "+Commit.Tree+"\n"+string.Concat(Commit.Parents.Select(p=>"parent "+p+"\n"))+"author "+Identity(Commit.AuthorName,Commit.AuthorEmail,Commit.AuthorDate)+"\ncommitter "+Identity(Commit.CommitterName,Commit.CommitterEmail,Commit.CommitterDate)+"\n\n"+Commit.Message;
  if(Hash("commit",Encoding.UTF8.GetBytes(raw))!=expectedTip)throw new InvalidDataException("Commit encoding cannot be reproduced exactly.");
  var known=new HashSet<string>();var total=0;foreach(var obj in Objects){if(!Regex.IsMatch(obj.Sha,"^[0-9a-f]{40}$")||!known.Add(obj.Sha))throw new InvalidDataException("Invalid or duplicate Git object.");
   if(obj.Type=="blob"){var data=Convert.FromBase64String(obj.Base64??throw new InvalidDataException("Blob absent."));total+=data.Length;if(total>MaxBytes||Hash("blob",data)!=obj.Sha)throw new InvalidDataException("Blob hash or size mismatch.");}
   else if(obj.Type=="tree"){if(obj.Entries==null||obj.Entries.Any(e=>!known.Contains(e.Sha)||e.Path.Length==0||e.Path is "." or ".."||e.Path.Contains('/')||e.Path.Contains('\\')||e.Path.Any(char.IsControl)||(e.Mode=="40000"?e.Type!="tree":e.Type!="blob")||e.Mode is not("40000" or "100644" or "100755" or "120000")))throw new InvalidDataException("Tree dependency or path invalid.");using var stream=new MemoryStream();foreach(var e in obj.Entries){stream.Write(Encoding.UTF8.GetBytes(e.Mode+" "+e.Path+"\0"));stream.Write(Convert.FromHexString(e.Sha));}var data=stream.ToArray();total+=data.Length;if(total>MaxBytes||Hash("tree",data)!=obj.Sha)throw new InvalidDataException("Tree hash or size mismatch.");}
   else throw new InvalidDataException("Unsupported Git object.");}
 }
}

