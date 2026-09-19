// Native Illustrator path construction. No raster objects or background shapes.
#target illustrator
(function(){
var root = new Folder("D:/WebstormProjects/NPEduTools/images/branding");
var data = [{"key": "standard", "file": "npedutools-logo", "paths": [{"name": "Lower folded sheet", "color": "#1C604D", "points": [{"a": [273.0, 337.0], "l": [273.0, 337.0], "r": [274.0, 444.0]}, {"a": [452.0, 524.0], "l": [315.0, 482.0], "r": [591.0, 567.0]}, {"a": [654.0, 779.0], "l": [654.0, 640.0], "r": [654.0, 779.0]}, {"a": [654.0, 997.0], "l": [654.0, 997.0], "r": [654.0, 1010.0]}, {"a": [639.0, 1013.0], "l": [651.0, 1013.0], "r": [639.0, 1013.0]}, {"a": [605.0, 1013.0], "l": [605.0, 1013.0], "r": [414.0, 1013.0]}, {"a": [273.0, 812.0], "l": [273.0, 960.0], "r": [273.0, 812.0]}]}, {"name": "Front sheet", "color": "#147D68", "points": [{"a": [273.0, 337.0], "l": [273.0, 337.0], "r": [274.0, 444.0]}, {"a": [452.0, 524.0], "l": [315.0, 482.0], "r": [481.0, 544.0]}, {"a": [492.0, 612.0], "l": [492.0, 570.0], "r": [492.0, 612.0]}, {"a": [492.0, 895.0], "l": [492.0, 895.0], "r": [492.0, 966.0]}, {"a": [605.0, 1013.0], "l": [526.0, 1007.0], "r": [414.0, 1013.0]}, {"a": [273.0, 812.0], "l": [273.0, 960.0], "r": [273.0, 812.0]}]}, {"name": "Upper turned page", "color": "#147D68", "points": [{"a": [273.0, 337.0], "l": [273.0, 401.0], "r": [273.0, 337.0]}, {"a": [273.0, 239.0], "l": [273.0, 239.0], "r": [273.0, 212.0]}, {"a": [298.0, 220.0], "l": [284.0, 208.0], "r": [329.0, 247.0]}, {"a": [432.0, 272.0], "l": [378.0, 262.0], "r": [504.0, 286.0]}, {"a": [589.0, 388.0], "l": [558.0, 326.0], "r": [510.0, 409.0]}, {"a": [389.0, 454.0], "l": [469.0, 450.0], "r": [323.0, 452.0]}]}, {"name": "Light green ribbon", "color": "#73C99B", "points": [{"a": [389.0, 454.0], "l": [430.0, 459.0], "r": [469.0, 450.0]}, {"a": [589.0, 388.0], "l": [510.0, 409.0], "r": [642.0, 370.0]}, {"a": [750.0, 360.0], "l": [697.0, 360.0], "r": [892.0, 360.0]}, {"a": [986.0, 579.0], "l": [986.0, 441.0], "r": [986.0, 579.0]}, {"a": [986.0, 974.0], "l": [986.0, 974.0], "r": [986.0, 996.0]}, {"a": [964.0, 1000.0], "l": [979.0, 1003.0], "r": [861.0, 981.0]}, {"a": [793.0, 812.0], "l": [793.0, 918.0], "r": [793.0, 812.0]}, {"a": [793.0, 646.0], "l": [793.0, 646.0], "r": [793.0, 500.0]}, {"a": [536.0, 459.0], "l": [722.0, 459.0], "r": [474.0, 459.0]}]}]}, {"key": "small", "file": "npedutools-logo-small", "paths": [{"name": "Simplified lower sheet", "color": "#147D68", "points": [{"a": [273.0, 394.0], "l": [273.0, 394.0], "r": [290.0, 470.0]}, {"a": [450.0, 539.0], "l": [342.0, 505.0], "r": [580.0, 579.0]}, {"a": [640.0, 779.0], "l": [640.0, 648.0], "r": [640.0, 779.0]}, {"a": [640.0, 995.0], "l": [640.0, 995.0], "r": [640.0, 1009.0]}, {"a": [623.0, 1013.0], "l": [636.0, 1013.0], "r": [623.0, 1013.0]}, {"a": [605.0, 1013.0], "l": [605.0, 1013.0], "r": [414.0, 1013.0]}, {"a": [273.0, 812.0], "l": [273.0, 960.0], "r": [273.0, 812.0]}]}, {"name": "Upper turned page", "color": "#147D68", "points": [{"a": [273.0, 337.0], "l": [273.0, 401.0], "r": [273.0, 337.0]}, {"a": [273.0, 239.0], "l": [273.0, 239.0], "r": [273.0, 212.0]}, {"a": [298.0, 220.0], "l": [284.0, 208.0], "r": [329.0, 247.0]}, {"a": [432.0, 272.0], "l": [378.0, 262.0], "r": [504.0, 286.0]}, {"a": [589.0, 388.0], "l": [558.0, 326.0], "r": [510.0, 409.0]}, {"a": [389.0, 454.0], "l": [469.0, 450.0], "r": [323.0, 452.0]}]}, {"name": "Open inner ribbon", "color": "#73C99B", "points": [{"a": [389.0, 454.0], "l": [430.0, 459.0], "r": [469.0, 450.0]}, {"a": [589.0, 388.0], "l": [510.0, 409.0], "r": [642.0, 370.0]}, {"a": [750.0, 360.0], "l": [697.0, 360.0], "r": [892.0, 360.0]}, {"a": [986.0, 579.0], "l": [986.0, 441.0], "r": [986.0, 579.0]}, {"a": [986.0, 974.0], "l": [986.0, 974.0], "r": [986.0, 996.0]}, {"a": [964.0, 1000.0], "l": [979.0, 1003.0], "r": [870.0, 981.0]}, {"a": [808.0, 812.0], "l": [808.0, 918.0], "r": [808.0, 812.0]}, {"a": [808.0, 646.0], "l": [808.0, 646.0], "r": [808.0, 497.0]}, {"a": [536.0, 459.0], "l": [722.0, 459.0], "r": [474.0, 459.0]}]}]}, {"key": "mono", "file": "npedutools-logo-mono", "paths": [{"name": "Lower silhouette", "color": "#202E35", "points": [{"a": [273.0, 394.0], "l": [273.0, 394.0], "r": [290.0, 470.0]}, {"a": [450.0, 539.0], "l": [342.0, 505.0], "r": [580.0, 579.0]}, {"a": [640.0, 779.0], "l": [640.0, 648.0], "r": [640.0, 779.0]}, {"a": [640.0, 995.0], "l": [640.0, 995.0], "r": [640.0, 1009.0]}, {"a": [623.0, 1013.0], "l": [636.0, 1013.0], "r": [623.0, 1013.0]}, {"a": [605.0, 1013.0], "l": [605.0, 1013.0], "r": [414.0, 1013.0]}, {"a": [273.0, 812.0], "l": [273.0, 960.0], "r": [273.0, 812.0]}]}, {"name": "Upper silhouette", "color": "#202E35", "points": [{"a": [273.0, 337.0], "l": [273.0, 401.0], "r": [273.0, 337.0]}, {"a": [273.0, 239.0], "l": [273.0, 239.0], "r": [273.0, 212.0]}, {"a": [298.0, 220.0], "l": [284.0, 208.0], "r": [329.0, 247.0]}, {"a": [432.0, 272.0], "l": [378.0, 262.0], "r": [504.0, 286.0]}, {"a": [589.0, 388.0], "l": [558.0, 326.0], "r": [642.0, 370.0]}, {"a": [750.0, 360.0], "l": [697.0, 360.0], "r": [892.0, 360.0]}, {"a": [986.0, 579.0], "l": [986.0, 441.0], "r": [986.0, 579.0]}, {"a": [986.0, 974.0], "l": [986.0, 974.0], "r": [986.0, 996.0]}, {"a": [964.0, 1000.0], "l": [979.0, 1003.0], "r": [870.0, 981.0]}, {"a": [808.0, 812.0], "l": [808.0, 918.0], "r": [808.0, 812.0]}, {"a": [808.0, 646.0], "l": [808.0, 646.0], "r": [808.0, 497.0]}, {"a": [536.0, 459.0], "l": [722.0, 459.0], "r": [474.0, 459.0]}, {"a": [389.0, 454.0], "l": [430.0, 459.0], "r": [323.0, 452.0]}]}]}, {"key": "mono-light", "file": "npedutools-logo-mono-light", "paths": [{"name": "Lower silhouette", "color": "#FFFFFF", "points": [{"a": [273.0, 394.0], "l": [273.0, 394.0], "r": [290.0, 470.0]}, {"a": [450.0, 539.0], "l": [342.0, 505.0], "r": [580.0, 579.0]}, {"a": [640.0, 779.0], "l": [640.0, 648.0], "r": [640.0, 779.0]}, {"a": [640.0, 995.0], "l": [640.0, 995.0], "r": [640.0, 1009.0]}, {"a": [623.0, 1013.0], "l": [636.0, 1013.0], "r": [623.0, 1013.0]}, {"a": [605.0, 1013.0], "l": [605.0, 1013.0], "r": [414.0, 1013.0]}, {"a": [273.0, 812.0], "l": [273.0, 960.0], "r": [273.0, 812.0]}]}, {"name": "Upper silhouette", "color": "#FFFFFF", "points": [{"a": [273.0, 337.0], "l": [273.0, 401.0], "r": [273.0, 337.0]}, {"a": [273.0, 239.0], "l": [273.0, 239.0], "r": [273.0, 212.0]}, {"a": [298.0, 220.0], "l": [284.0, 208.0], "r": [329.0, 247.0]}, {"a": [432.0, 272.0], "l": [378.0, 262.0], "r": [504.0, 286.0]}, {"a": [589.0, 388.0], "l": [558.0, 326.0], "r": [642.0, 370.0]}, {"a": [750.0, 360.0], "l": [697.0, 360.0], "r": [892.0, 360.0]}, {"a": [986.0, 579.0], "l": [986.0, 441.0], "r": [986.0, 579.0]}, {"a": [986.0, 974.0], "l": [986.0, 974.0], "r": [986.0, 996.0]}, {"a": [964.0, 1000.0], "l": [979.0, 1003.0], "r": [870.0, 981.0]}, {"a": [808.0, 812.0], "l": [808.0, 918.0], "r": [808.0, 812.0]}, {"a": [808.0, 646.0], "l": [808.0, 646.0], "r": [808.0, 497.0]}, {"a": [536.0, 459.0], "l": [722.0, 459.0], "r": [474.0, 459.0]}, {"a": [389.0, 454.0], "l": [430.0, 459.0], "r": [323.0, 452.0]}]}]}];
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
