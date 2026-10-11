#target illustrator
(function(){
  var root=new Folder('D:/CodeProjects/NPEduTools/images/promo/2026-10-07-leap');
  var oldUI=app.userInteractionLevel,lines=[];
  function log(s){lines.push(s);var f=new File(root.fsName+'/typography-v2-verification.txt');f.encoding='UTF-8';f.open('w');f.write(lines.join('\n'));f.close();}
  function rgb(hex){var c=new RGBColor();c.red=parseInt(hex.substr(1,2),16);c.green=parseInt(hex.substr(3,2),16);c.blue=parseInt(hex.substr(5,2),16);return c;}
  try{
    app.userInteractionLevel=UserInteractionLevel.DONTDISPLAYALERTS;
    var source=new File(root.fsName+'/NPEduTools-跃迁.ai');var target=new File(root.fsName+'/NPEduTools-跃迁-v2.ai');
    if(!source.copy(target.fsName))throw Error('Could not copy the original AI');
    var doc=app.open(target);var H=1080;
    function change(value,font,size,tracking,x,y,right){
      var old=null;for(var i=0;i<doc.textFrames.length;i++)if(doc.textFrames[i].contents===value){old=doc.textFrames[i];break;}
      if(!old)throw Error('Text not found: '+value);
      var oldC=old.textRange.characterAttributes;
      log(value+' BEFORE font='+oldC.textFont.name+' size='+oldC.size+' scale='+oldC.horizontalScale+'/'+oldC.verticalScale+' bounds='+old.visibleBounds.join(','));
      var parent=old.parent;var t=parent.textFrames.add();t.contents=value;
      var c=t.textRange.characterAttributes;c.textFont=app.textFonts.getByName(font);c.size=size;c.tracking=tracking;
      c.horizontalScale=100;c.verticalScale=100;c.fillColor=oldC.fillColor;t.opacity=old.opacity;t.position=[x,H-y];
      // Measure actual letter outlines, then translate without transforming the glyphs.
      var measure=t.duplicate();var outline=measure.createOutline();var bounds=outline.visibleBounds;
      var dx=right?x-bounds[2]:x-bounds[0],dy=H-y-bounds[1];outline.remove();t.translate(dx,dy);
      old.remove();log(value+' AFTER font='+c.textFont.name+' size='+c.size+' scale='+c.horizontalScale+'/'+c.verticalScale+' tracking='+c.tracking+' bounds='+t.visibleBounds.join(','));
      return t;
    }
    change('星火动力','MiSans-Bold',37,30,161,64,false);
    change('novark.ink','MiSans-Regular',34,0,2476,66,true);
    change('NPEduTools','MiSans-Demibold',45,-10,254,704,false);
    doc.selection=null;
    var save=new IllustratorSaveOptions();save.pdfCompatible=true;save.compressed=true;save.embedICCProfile=true;
    doc.saveAs(target,save);
    var png=new ExportOptionsPNG24();png.antiAliasing=true;png.transparency=false;png.artBoardClipping=true;png.horizontalScale=100;png.verticalScale=100;
    doc.exportFile(new File(root.fsName+'/NPEduTools-跃迁-v2.png'),ExportType.PNG24,png);
    var psd=new ExportOptionsPhotoshop();psd.resolution=72;psd.imageColorSpace=ImageColorSpace.RGB;psd.writeLayers=true;psd.writeTextEditability=true;psd.maximumEditability=true;psd.antiAliasing=true;
    doc.exportFile(new File(root.fsName+'/NPEduTools-跃迁-v2-中间.psd'),ExportType.PHOTOSHOP,psd);
    app.executeMenuCommand('fitall');doc.save();log('DONE / '+doc.layers.length+' layers / '+doc.textFrames.length+' live text frames / PNG 2560x1080');
  }catch(e){log('ERROR line '+e.line+': '+e);}
  finally{app.userInteractionLevel=oldUI;}
})();
