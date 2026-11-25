// Read-only HTTP smoke checks; never uploads or changes quota policy.
for (const url of [
 'https://bimaestro-family-library.bimaestro-community.workers.dev/v1/families?revitVersion=2025',
 'https://bimaestro-maquette-pilot.bimaestro-community.workers.dev/'
]) {
 const response=await fetch(url,{signal:AbortSignal.timeout(30000)});
 const body=await response.text();
 let json;try{json=JSON.parse(body);}catch{}
 console.log(JSON.stringify({url,status:response.status,keys:json?Object.keys(json):[],families:json?.items?.length,error:json?.error,body:json?undefined:body.slice(0,200)}));
}
