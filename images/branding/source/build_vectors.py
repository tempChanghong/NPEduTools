"""Hand-drawn cubic geometry, shared by clean SVGs and native Illustrator paths.
Coordinates follow the reference image; no auto-trace or embedded raster artwork.
Run this script, then run build-illustrator.jsx in Illustrator.
"""
from pathlib import Path
import json
import re

ROOT = Path(__file__).resolve().parents[1]
MAIN = '#147D68'
FOLD = '#1C604D'
LEAF = '#73C99B'

UPPER = '''M 273 337 L 273 239
C 273 212 284 208 298 220
C 329 247 378 262 432 272
C 504 286 558 326 589 388
C 510 409 469 450 389 454
C 323 452 273 401 273 337 Z'''
LOWER = '''M 273 337
C 274 444 315 482 452 524
C 591 567 654 640 654 779
L 654 997 C 654 1010 651 1013 639 1013
L 605 1013 C 414 1013 273 960 273 812
L 273 337 Z'''
FRONT = '''M 273 337
C 274 444 315 482 452 524
C 481 544 492 570 492 612
L 492 895 C 492 966 526 1007 605 1013
C 414 1013 273 960 273 812 L 273 337 Z'''
RIGHT = '''M 389 454
C 469 450 510 409 589 388
C 642 370 697 360 750 360
C 892 360 986 441 986 579
L 986 974 C 986 996 979 1003 964 1000
C 861 981 793 918 793 812
L 793 646 C 793 500 722 459 536 459
C 474 459 430 459 389 454 Z'''
# The lower sheet is deliberately detached by a 57-unit opening at the left.
# The interior stem gap grows from 139 to 168 units. The fold is removed.
SMALL_LOWER = '''M 273 394
C 290 470 342 505 450 539
C 580 579 640 648 640 779
L 640 995 C 640 1009 636 1013 623 1013
L 605 1013 C 414 1013 273 960 273 812
L 273 394 Z'''
SMALL_RIGHT = RIGHT.replace('861 981 793 918 793 812', '870 981 808 918 808 812').replace('L 793 646 C 793 500', 'L 808 646 C 808 497')
MONO_UPPER_RIGHT = '''M 273 337 L 273 239
C 273 212 284 208 298 220
C 329 247 378 262 432 272
C 504 286 558 326 589 388
C 642 370 697 360 750 360
C 892 360 986 441 986 579
L 986 974 C 986 996 979 1003 964 1000
C 870 981 808 918 808 812
L 808 646 C 808 497 722 459 536 459
C 474 459 430 459 389 454
C 323 452 273 401 273 337 Z'''

variants = [
 {'key':'standard','file':'npedutools-logo','paths':[
  ['Lower folded sheet',FOLD,LOWER],['Front sheet',MAIN,FRONT],
  ['Upper turned page',MAIN,UPPER],['Light green ribbon',LEAF,RIGHT]]},
 {'key':'small','file':'npedutools-logo-small','paths':[
  ['Simplified lower sheet',MAIN,SMALL_LOWER],['Upper turned page',MAIN,UPPER],
  ['Open inner ribbon',LEAF,SMALL_RIGHT]]},
 {'key':'mono','file':'npedutools-logo-mono','paths':[
  ['Lower silhouette','#202E35',SMALL_LOWER],['Upper silhouette','#202E35',MONO_UPPER_RIGHT]]},
 {'key':'mono-light','file':'npedutools-logo-mono-light','paths':[
  ['Lower silhouette','#FFFFFF',SMALL_LOWER],['Upper silhouette','#FFFFFF',MONO_UPPER_RIGHT]]}
]

def points(d):
    tokens = re.findall(r'[MLCZ]|-?\d+(?:\.\d+)?', d)
    pts=[]; i=0
    while i<len(tokens):
        op=tokens[i]; i+=1
        if op=='Z': break
        n=6 if op=='C' else 2
        nums=list(map(float,tokens[i:i+n])); i+=n
        a=nums[-2:]
        p={'a':a,'l':a[:],'r':a[:]}
        if op=='C':
            pts[-1]['r']=nums[:2]
            p['l']=nums[2:4]
        pts.append(p)
    if pts[-1]['a']==pts[0]['a']:
        pts[0]['l']=pts[-1]['l']; pts.pop()
    return pts

for v in variants:
    parts=['<?xml version="1.0" encoding="UTF-8"?>',
           '<svg xmlns="http://www.w3.org/2000/svg" width="1024" height="1024" viewBox="169.5 153.5 920 920" role="img" aria-labelledby="title desc">',
           '<title id="title">NPEduTools — '+v['key']+'</title>',
           '<desc id="desc">Editable hand-drawn cubic paths. Transparent negative spaces. No embedded images.</desc>']
    for j,(name,color,d) in enumerate(v['paths']):
        fill='currentColor' if v['key']=='mono' else color
        parts.append(f'<path id="shape-{j+1}" aria-label="{name}" fill="{fill}" d="{" ".join(d.split())}"/>')
    parts.append('</svg>')
    if v['key']=='mono': parts[1]=parts[1].replace('role="img"','color="#202E35" role="img"')
    (ROOT/(v['file']+'.svg')).write_text('\n'.join(parts)+'\n',encoding='utf-8')

data=[dict(key=v['key'],file=v['file'],paths=[dict(name=n,color=c,points=points(d)) for n,c,d in v['paths']]) for v in variants]
(ROOT/'source'/'geometry.json').write_text(json.dumps(data,indent=2),encoding='utf-8')
jsx = r'''// Native Illustrator path construction. No raster objects or background shapes.
#target illustrator
(function(){
var root = new Folder(__ROOT__);
var data = __DATA__;
var oldUI=app.userInteractionLevel;
app.userInteractionLevel=UserInteractionLevel.DONTDISPLAYALERTS;
function rgb(hex){var c=new RGBColor();c.red=parseInt(hex.substr(1,2),16);c.green=parseInt(hex.substr(3,2),16);c.blue=parseInt(hex.substr(5,2),16);return c;}
function xy(p,offset){return [(p[0]-169.5)*1024/920+offset,1024-(p[1]-153.5)*1024/920];}
function draw(doc,v,offset){
 var layer=doc.layers.add();layer.name=v.key+' / editable Bezier shapes';
 for(var j=0;j<v.paths.length;j++){
  var spec=v.paths[j],p=layer.pathItems.add();p.name=spec.name;
  p.filled=true;p.stroked=false;p.fillColor=rgb(spec.color);
  for(var k=0;k<spec.points.length;k++){
   var pt=spec.points[k],q=p.pathPoints.add();
   q.anchor=xy(pt.a,offset);q.leftDirection=xy(pt.l,offset);q.rightDirection=xy(pt.r,offset);
   q.pointType=PointType.CORNER;
  }p.closed=true;
 }
 return layer;
}
var master=app.documents.add(DocumentColorSpace.RGB,1024,1024);
master.rulerUnits=RulerUnits.Pixels;
var report=[];
for(var i=0;i<data.length;i++){
 var offset=i*1120;
 if(i>0)master.artboards.add([offset,1024,offset+1024,0]);
 master.artboards[i].name=data[i].key;
 draw(master,data[i],offset);
 report.push(data[i].key+': '+data[i].paths.length+' paths');
}
master.artboards.setActiveArtboardIndex(0);
var opt=new IllustratorSaveOptions();opt.pdfCompatible=true;opt.compressed=true;opt.embedICCProfile=true;
master.saveAs(new File(root.fsName+'/npedutools-logo.ai'),opt);
var renderDir=new Folder(root.fsName+'/source/renders');renderDir.create();
for(var i=0;i<data.length;i++){
 master.artboards.setActiveArtboardIndex(i);
 var sizes=(data[i].key=='standard')?[1024,256]:[256,64,48,32,24,16];
 for(var j=0;j<sizes.length;j++){
  var size=sizes[j],factor=size<=64?4:1;
  var exp=new ExportOptionsPNG24();exp.antiAliasing=true;exp.transparency=true;exp.artBoardClipping=true;
  exp.horizontalScale=100*size*factor/1024;exp.verticalScale=exp.horizontalScale;
  master.exportFile(new File(renderDir.fsName+'/'+data[i].key+'-'+size+'.png'),ExportType.PNG24,exp);
 }
}
master.artboards.setActiveArtboardIndex(0);
master.selection=null;
app.executeMenuCommand('fitall');
master.save();
var log=new File(root.fsName+'/source/illustrator-build.txt');log.open('w');
log.writeln('Illustrator '+app.version+' | RGB | 4 artboards | '+master.pathItems.length+' paths | '+master.rasterItems.length+' raster items | '+master.placedItems.length+' placed items');
for(var i=0;i<report.length;i++)log.writeln(report[i]);
log.close();app.userInteractionLevel=oldUI;
})();
'''.replace('__ROOT__',json.dumps(str(ROOT).replace('\\','/'))).replace('__DATA__',json.dumps(data))
(ROOT/'source'/'build-illustrator.jsx').write_text(jsx,encoding='utf-8')
print(json.dumps({v['key']:{'paths':len(v['paths']),'anchors':sum(len(points(p[2])) for p in v['paths'])} for v in variants},indent=2))
