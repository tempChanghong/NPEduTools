#target illustrator
(function(){
  var root=new Folder('D:/CodeProjects/NPEduTools/images/promo/2026-10-07-leap');
  var previous=app.userInteractionLevel,lines=[];
  function log(s){lines.push(s);var f=new File(root.fsName+'/layout-v3-verification.txt');f.encoding='UTF-8';f.open('w');f.write(lines.join('\n'));f.close();}
  function mint(){var c=new RGBColor();c.red=227;c.green=251;c.blue=239;return c;}
  function paint(item){if(item.typename==='PathItem'){item.filled=true;item.fillColor=mint();item.stroked=false;}else if(item.typename==='CompoundPathItem'){for(var i=0;i<item.pathItems.length;i++)paint(item.pathItems[i]);}else if(item.typename==='GroupItem'){for(var i=0;i<item.pageItems.length;i++)if(item.pageItems[i].parent===item)paint(item.pageItems[i]);}}
  try{
    app.userInteractionLevel=UserInteractionLevel.DONTDISPLAYALERTS;
    var source=new File(root.fsName+'/NPEduTools-跃迁-v2.ai'),target=new File(root.fsName+'/NPEduTools-跃迁-v3.ai');
    if(!source.copy(target.fsName))throw Error('Copy failed');var doc=app.open(target),brand=null;
    for(var i=0;i<doc.layers.length;i++)if(doc.layers[i].name.indexOf('08 /')===0)brand=doc.layers[i];
    if(!brand)throw Error('Brand layer missing');
    var old=null;for(var i=0;i<doc.textFrames.length;i++)if(doc.textFrames[i].contents==='星火动力')old=doc.textFrames[i];
    if(!old)throw Error('Brand text missing');old.remove();
    var asset='D:/Archives/文献集/NP/LOGO/星火动力中文字黑色.svg';
    var src=app.open(new File(asset));app.executeMenuCommand('selectall');app.executeMenuCommand('copy');
    app.activeDocument=doc;doc.activeLayer=brand;doc.selection=null;app.executeMenuCommand('paste');
    var selected=doc.selection,items=[];for(var i=0;i<selected.length;i++)items.push(selected[i]);
    var word=brand.groupItems.add();word.name='星火动力 / original vector wordmark';
    for(var i=items.length-1;i>=0;i--)items[i].move(word,ElementPlacement.PLACEATBEGINNING);
    src.close(SaveOptions.DONOTSAVECHANGES);app.activeDocument=doc;doc.selection=null;paint(word);
    var b=word.visibleBounds,scale=62/(b[1]-b[3])*100;word.resize(scale,scale);b=word.visibleBounds;word.translate(161-b[0],1080-51-b[1]);
    log('Wordmark source: '+asset+' / uniform scale '+scale+' / bounds '+word.visibleBounds.join(','));
    for(var i=0;i<doc.layers.length;i++){
      var l=doc.layers[i];if(l.name.indexOf('06 /')===0||l.name.indexOf('07 /')===0||l.name.indexOf('04 /')===0){
        var top=[];for(var j=0;j<l.pageItems.length;j++)if(l.pageItems[j].parent===l)top.push(l.pageItems[j]);
        for(var j=0;j<top.length;j++)top[j].translate(-64,0);log(l.name+' / moved left 64 px / '+top.length+' root objects');
      }
    }
    for(var i=0;i<doc.textFrames.length;i++){var t=doc.textFrames[i];log('Text '+t.contents+' / bounds '+t.visibleBounds.join(','));}
    doc.selection=null;var save=new IllustratorSaveOptions();save.pdfCompatible=true;save.compressed=true;save.embedICCProfile=true;doc.saveAs(target,save);
    var png=new ExportOptionsPNG24();png.antiAliasing=true;png.transparency=false;png.artBoardClipping=true;png.horizontalScale=100;png.verticalScale=100;
    doc.exportFile(new File(root.fsName+'/NPEduTools-跃迁-v3.png'),ExportType.PNG24,png);
    var psd=new ExportOptionsPhotoshop();psd.resolution=72;psd.imageColorSpace=ImageColorSpace.RGB;psd.writeLayers=true;psd.writeTextEditability=true;psd.maximumEditability=true;psd.antiAliasing=true;
    doc.exportFile(new File(root.fsName+'/NPEduTools-跃迁-v3-中间.psd'),ExportType.PHOTOSHOP,psd);
    app.executeMenuCommand('fitall');doc.save();log('DONE / '+doc.layers.length+' layers / '+doc.textFrames.length+' live text frames');
  }catch(e){log('ERROR line '+e.line+': '+e);}finally{app.userInteractionLevel=previous;}
})();
