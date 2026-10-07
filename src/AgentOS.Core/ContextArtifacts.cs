using System.Buffers.Binary;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
namespace AgentOS.Core;
public enum ContextArtifactKind { Text, Png }
public sealed record ContextArtifactRef(string Id,ContextArtifactKind Kind,string Sha256,string Source,DateTimeOffset CapturedAt);
public sealed class ContextArtifacts
{
 public const int MaxTextBytes=16*1024,MaxImageBytes=8*1024*1024,MaxWireBytes=12*1024*1024;
 readonly string root;
 public ContextArtifacts(string dataRoot)
 {
  if(!Directory.Exists(dataRoot)||(File.GetAttributes(dataRoot)&FileAttributes.ReparsePoint)!=0)throw new IOException("Project data root missing or linked.");
  root=Path.Combine(dataRoot,"context-artifacts");Directory.CreateDirectory(root);EnsureRoot();if(OperatingSystem.IsWindows()){var owner=WindowsIdentity.GetCurrent().User??throw new IOException("Current user unavailable.");var acl=new DirectorySecurity();acl.SetAccessRuleProtection(true,false);acl.SetOwner(owner);acl.AddAccessRule(new FileSystemAccessRule(owner,FileSystemRights.FullControl,InheritanceFlags.ContainerInherit|InheritanceFlags.ObjectInherit,PropagationFlags.None,AccessControlType.Allow));new DirectoryInfo(root).SetAccessControl(acl);}
 }
 void EnsureRoot(){if(!Directory.Exists(root)||(File.GetAttributes(root)&FileAttributes.ReparsePoint)!=0)throw new IOException("Context store missing or linked.");}
 static void Check(ContextArtifactKind kind,byte[] bytes)
 {
  if(kind is not (ContextArtifactKind.Text or ContextArtifactKind.Png)||bytes.Length==0||bytes.Length>(kind==ContextArtifactKind.Text?MaxTextBytes:MaxImageBytes))throw new ArgumentException("Context size exceeds limit.");
  if(kind==ContextArtifactKind.Text){_=new UTF8Encoding(false,true).GetString(bytes);return;}
  if(bytes.Length<45||!bytes.AsSpan(0,8).SequenceEqual(new byte[]{137,80,78,71,13,10,26,10}))throw new ArgumentException("Image must be PNG.");
  int offset=8;bool data=false,end=false;
  while(offset+12<=bytes.Length)
  {
   uint length=BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset,4));if(length>bytes.Length-offset-12)throw new ArgumentException("PNG chunk exceeds bounds.");
   var name=bytes.AsSpan(offset+4,4);var payload=bytes.AsSpan(offset+8,(int)length);uint crc=0xffffffff;
   foreach(byte octet in bytes.AsSpan(offset+4,4+(int)length)){crc^=octet;for(int bit=0;bit<8;bit++)crc=(crc>>1)^((crc&1)==0?0u:0xedb88320u);}
   if(~crc!=BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset+8+(int)length,4)))throw new ArgumentException("PNG checksum invalid.");
   if(offset==8){if(!name.SequenceEqual("IHDR"u8)||length!=13)throw new ArgumentException("PNG header invalid.");uint w=BinaryPrimitives.ReadUInt32BigEndian(payload[..4]),h=BinaryPrimitives.ReadUInt32BigEndian(payload.Slice(4,4));if(w==0||h==0||w>16384||h>16384||(ulong)w*h>32000000)throw new ArgumentException("PNG dimensions invalid.");}
   if(name.SequenceEqual("IDAT"u8))data=true;offset+=checked((int)length+12);if(name.SequenceEqual("IEND"u8)){end=length==0&&offset==bytes.Length;break;}
  }
  if(!data||!end)throw new ArgumentException("PNG data or ending missing.");
 }
 [StructLayout(LayoutKind.Sequential,Pack=4)] private struct FileInformation { public uint Attributes;public long Created,Accessed,Modified;public uint Volume,SizeHigh,SizeLow,Links,IndexHigh,IndexLow; }
 [DllImport("kernel32.dll",SetLastError=true)] private static extern bool GetFileInformationByHandle(SafeFileHandle handle,out FileInformation information);
 static byte[] ReadBounded(string path,int limit)
 {
  using var file=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);
  if(file.Length<1||file.Length>limit)throw new IOException("Context file exceeds bounds.");var bytes=new byte[checked((int)file.Length)];file.ReadExactly(bytes);if(file.ReadByte()!=-1)throw new IOException("Context file changed while reading.");return bytes;
 }
 public ContextArtifactRef Accept(ContextArtifactKind kind,byte[] bytes,string source,DateTimeOffset capturedAt)
 {
  EnsureRoot();Check(kind,bytes);if(string.IsNullOrWhiteSpace(source)||source.Length>512)throw new ArgumentException("Invalid context source.");
  string hash=Convert.ToHexString(SHA256.HashData(bytes)),path=Path.Combine(root,hash);
  if(!File.Exists(path)){string temp=Path.Combine(root,Guid.NewGuid().ToString("N")+".tmp");try{using(var file=new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None,4096,FileOptions.WriteThrough)){file.Write(bytes);file.Flush(true);}File.Move(temp,path,false);}catch(IOException)when(File.Exists(path)){}finally{if(File.Exists(temp))File.Delete(temp);}}
  var reference=new ContextArtifactRef(Guid.NewGuid().ToString("N"),kind,hash,source,capturedAt);using(var file=new FileStream(Path.Combine(root,reference.Id+".json"),FileMode.CreateNew,FileAccess.Write,FileShare.None))JsonSerializer.Serialize(file,reference);_=Read(reference);return reference;
 }
 public byte[] Read(ContextArtifactRef reference)
 {
  EnsureRoot();if(!Guid.TryParseExact(reference.Id,"N",out _)||reference.Sha256?.Length!=64||!reference.Sha256.All(Uri.IsHexDigit))throw new InvalidDataException("Invalid context reference.");
  var citation=Path.Combine(root,reference.Id+".json");var path=Path.Combine(root,reference.Sha256);var ci=new FileInfo(citation);var fi=new FileInfo(path);
  if(!ci.Exists||ci.Length>4096||(ci.Attributes&FileAttributes.ReparsePoint)!=0||!fi.Exists||fi.Length>MaxImageBytes||(fi.Attributes&FileAttributes.ReparsePoint)!=0)throw new IOException("Accepted context missing or linked.");
  if(JsonSerializer.Deserialize<ContextArtifactRef>(ReadBounded(citation,4096))!=reference)throw new IOException("Context citation changed.");var bytes=ReadBounded(path,MaxImageBytes);if(Convert.ToHexString(SHA256.HashData(bytes))!=reference.Sha256)throw new IOException("Accepted context changed.");Check(reference.Kind,bytes);return bytes;
 }
 public void Verify(IEnumerable<ContextArtifactRef> references){var ids=new HashSet<string>(StringComparer.Ordinal);long total=0;foreach(var r in references){if(r==null||!ids.Add(r.Id)||ids.Count>32)throw new InvalidDataException("Duplicate or excessive context identities.");total+=Read(r).Length;if(total>MaxWireBytes)throw new InvalidDataException("Context total exceeds limit.");}}
 public void Prune(IEnumerable<ContextArtifactRef> retained){EnsureRoot();var refs=retained.ToArray();var keep=refs.Select(x=>x.Sha256).Concat(refs.Select(x=>x.Id+".json")).ToHashSet(StringComparer.Ordinal);foreach(var file in Directory.EnumerateFiles(root))if(!keep.Contains(Path.GetFileName(file))&&(File.GetAttributes(file)&FileAttributes.ReparsePoint)==0&&File.GetCreationTimeUtc(file)<DateTime.UtcNow.AddDays(-30))File.Delete(file);}
}
public sealed record ContextTurnPayload(List<object> Input,Dictionary<string,object> AdditionalContext)
{
 public static ContextTurnPayload Build(string task,IReadOnlyList<ContextArtifactRef> references,ContextArtifacts store)
 {
  store.Verify(references);var input=new List<object>{new{type="text",text=task}};var additional=new Dictionary<string,object>(StringComparer.Ordinal);
  foreach(var r in references){var bytes=store.Read(r);var value="Accepted context citation "+r.Id+"; captured: "+r.CapturedAt.ToString("O")+"; source: "+r.Source;if(r.Kind==ContextArtifactKind.Text)value+="\n"+Encoding.UTF8.GetString(bytes);additional.Add(r.Id,new{kind="untrusted",value});if(r.Kind==ContextArtifactKind.Png)input.Add(new{type="image",url="data:image/png;base64,"+Convert.ToBase64String(bytes)});}
  return new(input,additional);
 }
}
