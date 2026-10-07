using System.Text;
using System.Text.Json;
namespace AgentOS.App;
internal static class TranscriptReader
{
 public static async Task<string> ReadAsync(string path)
 {
  if(!File.Exists(path))return "No agent messages recorded yet.";
  try
  {
   await using var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete,4096,true);
   if(stream.Length>1_000_000)stream.Seek(-1_000_000,SeekOrigin.End);
   using var reader=new StreamReader(stream,Encoding.UTF8);if(stream.Position>0)await reader.ReadLineAsync();
   var messages=new List<string>();string? line;while((line=await reader.ReadLineAsync())!=null)
   {
    if(line.Length>262_144)continue;
    try
    {
     using var json=JsonDocument.Parse(line);var root=json.RootElement;
     if(root.TryGetProperty("method",out var method)&&method.GetString()=="item/completed"&&root.TryGetProperty("params",out var args)&&args.TryGetProperty("item",out var item)&&item.TryGetProperty("type",out var kind)&&kind.GetString()=="agentMessage"&&item.TryGetProperty("text",out var value))
     {var message=value.GetString();if(!string.IsNullOrWhiteSpace(message))messages.Add(message);}
    }
    catch(JsonException){} catch(InvalidOperationException){}
   }
   var result=string.Join("\n\n",messages.TakeLast(12));return result.Length>24_000?result[^24_000..]:string.IsNullOrWhiteSpace(result)?"No agent messages recorded yet.":result;
  }
  catch(IOException){return "Transcript is temporarily busy.";}
  catch(UnauthorizedAccessException){return "Transcript is unavailable.";}
 }
}

