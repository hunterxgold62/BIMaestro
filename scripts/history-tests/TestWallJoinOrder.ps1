$ErrorActionPreference='Stop'
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$source=[IO.File]::ReadAllText((Join-Path $repo 'BIMaestro/commands/qui est le coupable/ElementHistoryRelations.cs'))
$start=$source.IndexOf('internal static List<string> WallJoinPermutation(',[StringComparison]::Ordinal)
if($start -lt 0){throw 'Production wall join permutation helper missing.'}
$opening=$source.IndexOf('{',$start);$depth=1;$end=-1
for($i=$opening+1;$i -lt $source.Length;$i++){
    if($source[$i] -eq '{'){$depth++}
    if($source[$i] -eq '}'){$depth--}
    if(!$depth){$end=$i;break}
}
if($end -lt 0){throw 'Unterminated production helper.'}
$method=$source.Substring($start,$end-$start+1)
foreach($required in @('public List<string> JoinParticipants','public List<string> SecondJoinParticipants',
    'location.set_ElementsAtJoin(end, ordered);','using (var relationGroup = new TransactionGroup',
    'Verify(doc, relation, first, second);','relationGroup.Assimilate()')){
    if(!$source.Contains($required)){throw "Wall join integration missing: $required"}
}
if($source.IndexOf('Verify(doc, relation, first, second);') -gt $source.IndexOf('relationGroup.Assimilate()')){
    throw 'Relation must be verified before assimilation.'
}
$checks=@'
using System;
using System.Linq;
using System.Collections.Generic;
public static class WallJoinOrderChecks {
    static void Check(bool ok,string message){if(!ok)throw new Exception(message);}
    static void Reject(Action action,string message){try{action();}catch(InvalidOperationException){return;}throw new Exception(message);}
    public static string Run(){
        var current=new List<string>{"B","A","C"};
        var desired=WallJoinPermutation(current,new List<string>{"A","B","C"},"A",null,null);
        Check(desired.SequenceEqual(new[]{"A","B","C"}),"Full historical permutation must be exact");
        Check(current.SequenceEqual(new[]{"B","A","C"}),"The live participant list must not be mutated by planning");
        Check(WallJoinPermutation(current,null,"A",0,3).SequenceEqual(new[]{"A","B","C"}),"Legacy position works only against the retained participant count");
        Check(WallJoinPermutation(current,null,"B",2,3).SequenceEqual(new[]{"A","C","B"}),"Legacy move to final index must preserve every other participant");
        Reject(()=>WallJoinPermutation(current,null,"A",0,2),"A changed legacy count must be rejected");
        Reject(()=>WallJoinPermutation(current,null,"A",0,null),"An unrecorded legacy count must be rejected");
        Reject(()=>WallJoinPermutation(current,null,"A",3,3),"Out-of-range index must be rejected");
        Reject(()=>WallJoinPermutation(current,null,"missing",0,3),"A missing peer must not be introduced");
        Reject(()=>WallJoinPermutation(current,new List<string>{"A","B"},"A",null,null),"Historical permutation must not delete a new participant");
        Reject(()=>WallJoinPermutation(current,new List<string>{"A","B","D"},"A",null,null),"Historical permutation must not replace a participant");
        Reject(()=>WallJoinPermutation(current,new List<string>{"A","A","C"},"A",null,null),"Two historical identities must not collapse onto one participant");
        Reject(()=>WallJoinPermutation(new List<string>{"A","A"},null,"A",0,2),"Duplicate live identities must be rejected");
        Reject(()=>WallJoinPermutation(current,null,"A",null,3),"An absent historical order must not be declared restored");
        return "PASS: production wall-join permutation planning, exact participant sets, legacy count/index guards, bijection and immutability. Setter/capture and verify-before-assimilate route checked statically; Revit join geometry and rollback behavior require the real harness.";
    }
'@
Add-Type -TypeDefinition ($checks+"`n"+$method+"`n}")
[WallJoinOrderChecks]::Run()
