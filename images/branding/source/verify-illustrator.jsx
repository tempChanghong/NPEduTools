#target illustrator
(function(){
var root=new Folder('D:/WebstormProjects/NPEduTools/images/branding');
var old=app.userInteractionLevel;app.userInteractionLevel=UserInteractionLevel.DONTDISPLAYALERTS;
// Open the saved AI first, outside this script. Closing the last document and
// reopening it inside one script triggered an Illustrator 30.7 host crash.
var master=app.activeDocument;
var log=new File(root.fsName+'/source/illustrator-verification.txt');log.open('w');
log.writeln('Reopened saved native AI: '+master.artboards.length+' artboards, '+master.pathItems.length+' paths, '+master.rasterItems.length+' raster, '+master.placedItems.length+' placed.');
var anchors=0;for(var p=0;p<master.pathItems.length;p++)anchors+=master.pathItems[p].pathPoints.length;
log.writeln('Total editable anchors: '+anchors);
var names=['npedutools-logo','npedutools-logo-small','npedutools-logo-mono','npedutools-logo-mono-light'];
for(var k=0;k<names.length;k++){
 var doc=app.open(new File(root.fsName+'/'+names[k]+'.svg'));
 log.writeln(names[k]+'.svg: '+doc.pathItems.length+' paths, '+doc.rasterItems.length+' raster, '+doc.placedItems.length+' placed.');
 var o=new ExportOptionsPNG24();o.transparency=true;o.antiAliasing=true;o.artBoardClipping=true;o.horizontalScale=25;o.verticalScale=25;
 doc.exportFile(new File(root.fsName+'/source/renders/verify-'+names[k]+'.png'),ExportType.PNG24,o);
 doc.close(SaveOptions.DONOTSAVECHANGES);
}
var ref=app.open(new File(root.parent.fsName+'/logoorigin.png'));
log.writeln('Reference PNG opened separately in Illustrator: '+ref.width+' x '+ref.height+' pt. Never placed in vector master.');
ref.close(SaveOptions.DONOTSAVECHANGES);
master.activate();master.artboards.setActiveArtboardIndex(0);app.executeMenuCommand('fitin');
log.close();app.userInteractionLevel=old;
})();
