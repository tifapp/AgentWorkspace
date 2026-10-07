using System.Text;
using System.Text.RegularExpressions;
namespace AgentOS.Core;
internal static class PostgreSqlMigrationGuard
{
 internal static void Validate(string sql,string schema)
 {
  if(string.IsNullOrWhiteSpace(sql)||Encoding.UTF8.GetByteCount(sql)>1048576)throw new InvalidDataException("SQL size outside migration bound.");
  var statements=new List<List<string>>();var words=new List<string>();
  for(int i=0;i<sql.Length;){var c=sql[i];if(char.IsWhiteSpace(c)){i++;continue;}
   if(c=='-'&&i+1<sql.Length&&sql[i+1]=='-'){i+=2;while(i<sql.Length&&sql[i]!='\n')i++;continue;}
   if(c=='/'&&i+1<sql.Length&&sql[i+1]=='*'){i+=2;var depth=1;while(i<sql.Length&&depth>0){if(i+1<sql.Length&&sql[i]=='/'&&sql[i+1]=='*'){depth++;i+=2;}else if(i+1<sql.Length&&sql[i]=='*'&&sql[i+1]=='/'){depth--;i+=2;}else i++;}if(depth!=0)throw new InvalidDataException("Unclosed SQL comment.");continue;}
   if(c=='\''||c=='"'){var quote=c;i++;var closed=false;while(i<sql.Length){if(sql[i]=='\\')throw new InvalidDataException("Backslash SQL string syntax unsupported.");if(sql[i++]!=quote)continue;if(i<sql.Length&&sql[i]==quote){i++;continue;}closed=true;break;}if(!closed)throw new InvalidDataException("Unclosed SQL literal.");words.Add(quote=='"'?"IDENTIFIER":"LITERAL");continue;}
   if(c=='$'){var m=Regex.Match(sql[i..],@"^\$[A-Za-z_0-9]*\$");if(!m.Success)throw new InvalidDataException("Unsupported dollar token.");var end=sql.IndexOf(m.Value,i+m.Length,StringComparison.Ordinal);if(end<0)throw new InvalidDataException("Unclosed dollar string.");i=end+m.Length;words.Add("LITERAL");continue;}
   if(char.IsLetter(c)||c=='_'){var start=i++;while(i<sql.Length&&(char.IsLetterOrDigit(sql[i])||sql[i]=='_'))i++;words.Add(sql[start..i].ToUpperInvariant());continue;}
   if(char.IsDigit(c)){while(i<sql.Length&&(char.IsDigit(sql[i])||sql[i]=='.'))i++;words.Add("NUMBER");continue;}
   if(c==';'){if(words.Count>0){statements.Add(words);words=new();}i++;continue;}
   if("().,[]".Contains(c)){words.Add(c.ToString());i++;continue;}throw new InvalidDataException("Unsupported SQL syntax.");}
  if(words.Count>0)statements.Add(words);if(statements.Count is <1 or >32)throw new InvalidDataException("Migration statement count outside bound.");
  var denied=new HashSet<string>{"BEGIN","START","COMMIT","ROLLBACK","SAVEPOINT","RELEASE","PREPARE","EXECUTE","DO","CALL","COPY","SET","RESET","GRANT","REVOKE","EXTENSION","FUNCTION","PROCEDURE","TRIGGER","LANGUAGE","SECURITY","PROGRAM","FDW","SERVER","FOREIGN","IMPORT","SUBSCRIPTION","PUBLICATION","DATABASE","ROLE","USER","SCHEMA","TEMP","TEMPORARY","UNLOGGED","VACUUM","ANALYZE","LISTEN","NOTIFY","LOCK","SELECT","INSERT","UPDATE","DELETE","TRUNCATE","REFERENCES","OWNER","AUTHORIZATION","CONCURRENTLY","INHERITS","LIKE","PARTITION","TABLESPACE","USING","CHECK","GENERATED","IDENTITY","COLLATE"};
  foreach(var t in statements){if(t.Any(denied.Contains))throw new InvalidDataException("SQL exceeds approved migration scope.");var i=0;
   if(t[i++]=="CREATE"){if(i<t.Count&&t[i]=="UNIQUE")i++;if(i>=t.Count||t[i] is not("TABLE" or "INDEX"))throw new InvalidDataException("Only table and index DDL supported.");var kind=t[i++];if(i+2<t.Count&&t[i]=="IF"&&t[i+1]=="NOT"&&t[i+2]=="EXISTS")i+=3;if(kind=="INDEX"){if(i>=t.Count)throw new InvalidDataException("Index name required.");if(i+2<t.Count&&t[i+1]=="."){if(t[i]!=schema.ToUpperInvariant())throw new InvalidDataException("Index schema differs.");i+=3;}else i++;if(i>=t.Count||t[i++]!="ON")throw new InvalidDataException("Index table required.");}}
   else if(t[0]=="ALTER"&&t.Count>2&&t[1]=="TABLE"){i=2;if(i+1<t.Count&&t[i]=="IF"&&t[i+1]=="EXISTS")i+=2;}
   else throw new InvalidDataException("Only table and index DDL supported.");
   if(i+2>=t.Count||t[i]!=schema.ToUpperInvariant()||t[i+1]!="."||t[i+2] is "LITERAL" or "NUMBER")throw new InvalidDataException("Schema-qualified DDL target required.");
   for(var j=i+3;j+1<t.Count;j++)if(t[j+1]=="("&&t[j] is not("VARCHAR" or "CHAR" or "CHARACTER" or "NUMERIC" or "DECIMAL" or "TIMESTAMP" or "TIME" or "KEY" or "UNIQUE"))throw new InvalidDataException("Dynamic SQL expression unsupported.");
  }
 }
}



