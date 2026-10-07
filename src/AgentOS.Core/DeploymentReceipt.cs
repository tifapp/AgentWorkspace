using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text.Json;
namespace AgentOS.Core;
public sealed record DeploymentReceiptContract(string Endpoint,string CredentialTarget,string[] PinnedIps);
public sealed record DeploymentAppliedReceipt(string OperationId,string ScopeDigest,string EvidenceSha256,string ArtifactSha256,string CommandSha256,string EnvironmentSha256,string Destination,string Status);
public sealed class DeploymentReceiptProbe
{
 readonly IScopedCredentialProvider credentials;readonly HttpMessageHandler? transport;
 public DeploymentReceiptProbe(IScopedCredentialProvider credentials,HttpMessageHandler? transport=null){this.credentials=credentials;this.transport=transport;}
 public static void Validate(DeploymentReceiptContract? contract)
 {
  if(contract==null||!Uri.TryCreate(contract.Endpoint,UriKind.Absolute,out var uri)||uri.Scheme!="https"||uri.Port!=443||uri.UserInfo.Length!=0||uri.Query.Length!=0||uri.Fragment.Length!=0||uri.AbsolutePath.Contains("..")||contract.CredentialTarget!="AgentOS/Deployment/"+uri.Host.ToLowerInvariant()||contract.CredentialTarget.Length>256||contract.PinnedIps==null||contract.PinnedIps.Length is <1 or >8||contract.PinnedIps.Any(x=>!IPAddress.TryParse(x,out var ip)||IPAddress.IsLoopback(ip)||ip.IsIPv6LinkLocal||ip.IsIPv6Multicast||ip.Equals(IPAddress.Any)||ip.Equals(IPAddress.IPv6Any)))throw new InvalidDataException("Scoped HTTPS deployment receipt contract required.");
 }
 public async Task<EffectOutcome> CheckAsync(string id,EffectScope scope,DeploymentReceiptContract contract,CancellationToken ct)
 {
  Validate(contract);var endpoint=new Uri(contract.Endpoint.TrimEnd('/')+"/"+Uri.EscapeDataString(id));
  var token=credentials.GetSecret(contract.CredentialTarget);if(string.IsNullOrEmpty(token))return new(ExternalEffectState.Unknown,"Scoped receipt credential unavailable.");
  HttpMessageHandler handler=transport??new SocketsHttpHandler{AllowAutoRedirect=false,UseProxy=false,ConnectCallback=async(context,cancel)=>
  {
   // The approved IP ACL is the actual network scope. Shared IPs are not a domain boundary.
   var ip=IPAddress.Parse(contract.PinnedIps[0]);var socket=new Socket(ip.AddressFamily,SocketType.Stream,ProtocolType.Tcp);try{await socket.ConnectAsync(ip,context.DnsEndPoint.Port,cancel);return new NetworkStream(socket,true);}catch{socket.Dispose();throw;}
  }};
  using var client=new HttpClient(handler,transport==null){Timeout=TimeSpan.FromSeconds(15)};
  using var request=new HttpRequestMessage(HttpMethod.Get,endpoint);request.Headers.Authorization=new AuthenticationHeaderValue("Bearer",token);request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
  using var response=await client.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,ct);
  if(response.RequestMessage?.RequestUri!=endpoint||(int)response.StatusCode is >=300 and <400)throw new InvalidDataException("Receipt redirect rejected.");
  if(response.StatusCode==HttpStatusCode.NotFound)return new(ExternalEffectState.Unknown,"Applied receipt absent; no automatic retry.");
  if(response.StatusCode!=HttpStatusCode.OK||response.Content.Headers.ContentLength>65536)throw new InvalidDataException("Receipt lookup inconclusive.");
  await using var input=await response.Content.ReadAsStreamAsync(ct);using var buffer=new MemoryStream();var chunk=new byte[4096];int n;while((n=await input.ReadAsync(chunk,ct))!=0){if(buffer.Length+n>65536)throw new InvalidDataException("Receipt too large.");buffer.Write(chunk,0,n);}var bytes=buffer.ToArray();
  var receipt=JsonSerializer.Deserialize<DeploymentAppliedReceipt>(bytes)??throw new InvalidDataException("Receipt absent.");
  if(receipt.OperationId!=id||receipt.ScopeDigest!=scope.Digest||receipt.EvidenceSha256!=scope.EvidenceSha256||receipt.ArtifactSha256!=scope.ArtifactSha256||receipt.CommandSha256!=scope.CommandSha256||receipt.EnvironmentSha256!=scope.EnvironmentSha256||receipt.Destination!=scope.Destination||receipt.Status!="applied")return new(ExternalEffectState.Unknown,"Applied receipt differs from approved scope.");
  return new(ExternalEffectState.Completed,"Exact remote applied receipt confirmed.",id);
 }
}





