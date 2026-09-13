import assert from 'node:assert/strict';
import { validateAnalysis } from './analysis.ts';
assert.deepEqual(validateAnalysis({allowImplicitTerminals:false}),{allowImplicitTerminals:false});
assert.deepEqual(validateAnalysis({endpoints:{'connector-1':4}}),{endpoints:{'connector-1':4}});
for(const input of [null,[],{}, {allowImplicitTerminals:'false'}, {endpoints:{x:5}}, {endpoints:{x:1.1}}, {endpoints:{x:'2'}}, {unknown:1}, JSON.parse('{"endpoints":{"__proto__":1}}')]) assert.throws(()=>validateAnalysis(input));
console.log('MEP analysis settings validation passed.');
