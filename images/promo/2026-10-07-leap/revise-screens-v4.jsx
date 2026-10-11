#target illustrator
(function(){
  var root=new Folder('D:/CodeProjects/NPEduTools/images/promo/2026-10-07-leap');
  var previous=app.userInteractionLevel,lines=[];
  function log(s){lines.push(s);var f=new File(root.fsName+'/layout-v4-verification.txt');f.encoding='UTF-8';f.open('w');f.write(lines.join('\n'));f.close();}
  function mint(){var c=new RGBColor();c.red=227;c.green=251;c.blue=239;return c;}
  function paint(item){if(item.typename==='PathItem'){item.filled=true;item.fillColor=mint();item.stroked=false;}else if(item.typename==='CompoundPathItem'){for(var i=0;i<item.pathItems.length;i++)paint(item.pathItems[i]);}else if(item.typename==='GroupItem'){for(var i=0;i<item.pageItems.length;i++)if(item.pageItems[i].parent===item)paint(item.pageItems[i]);}}
  try{
    app.userInteractionLevel=UserInteractionLevel.DONTDISPLAYALERTS;
    var source=new File(root.fsName+'/NPEduTools-跃迁-v3.ai'),target=new File(root.fsName+'/NPEduTools-跃迁-v4.ai');
    if(!source.copy(target.fsName))throw Error('Copy failed');var doc=app.open(target),screens=null;
    for(var i=0;i<doc.layers.length;i++)if(doc.layers[i].name.indexOf('05 /')===0)screens=doc.layers[i];
    if(!screens)throw Error('Screenshot layer missing');
    var items=[];for(var i=0;i<screens.pageItems.length;i++)if(screens.pageItems[i].parent===screens)items.push(screens.pageItems[i]);
    for(var i=0;i<items.length;i++)items[i].translate(72,0);
    log('Screenshots moved right 72 px / '+items.length+' root objects');
    doc.selection=null;var save=new IllustratorSaveOptions();save.pdfCompatible=true;save.compressed=true;save.embedICCProfile=true;doc.saveAs(target,save);
    var png=new ExportOptionsPNG24();png.antiAliasing=true;png.transparency=false;png.artBoardClipping=true;png.horizontalScale=100;png.verticalScale=100;
    doc.exportFile(new File(root.fsName+'/NPEduTools-跃迁-v4.png'),ExportType.PNG24,png);
    var psd=new ExportOptionsPhotoshop();psd.resolution=72;psd.imageColorSpace=ImageColorSpace.RGB;psd.writeLayers=true;psd.writeTextEditability=true;psd.maximumEditability=true;psd.antiAliasing=true;
    doc.exportFile(new File(root.fsName+'/NPEduTools-跃迁-v4-中间.psd'),ExportType.PHOTOSHOP,psd);
    app.executeMenuCommand('fitall');doc.save();log('DONE / '+doc.layers.length+' layers / '+doc.textFrames.length+' live text frames');
  }catch(e){log('ERROR line '+e.line+': '+e);}finally{app.userInteractionLevel=previous;}
})();
