#target photoshop
(function(){
  var root=new Folder('D:/CodeProjects/NPEduTools/images/promo/2026-10-07-leap');
  var oldDialogs=app.displayDialogs,oldUnits=app.preferences.rulerUnits;
  var lines=[];
  function log(s){lines.push(s);var f=new File(root.fsName+'/photoshop-verification.txt');f.encoding='UTF-8';f.open('w');f.write(lines.join('\n'));f.close();}
  function find(layers,prefix){for(var i=0;i<layers.length;i++){var l=layers[i];if(l.name.indexOf(prefix)===0)return l;if(l.typename==='LayerSet'){var result=find(l.layers,prefix);if(result)return result;}}return null;}
  try{
    app.displayDialogs=DialogModes.NO;app.preferences.rulerUnits=Units.PIXELS;
    var doc=app.open(new File(root.fsName+'/NPEduTools-跃迁.psd'));
    log('Photoshop '+app.version+' opened Illustrator PSD');
    var bg=find(doc.layers,'01 /');if(!bg)throw Error('Background layer not found');
    var bounds=bg.bounds;log('Background bounds '+bounds.join(', '));
    doc.crop(bounds);
    log('Canvas '+doc.width.as('px')+' x '+doc.height.as('px'));
    var count=0,texts=0;
    function walk(layers,depth){for(var i=0;i<layers.length;i++){var l=layers[i];count++;log(depth+l.name+' / '+l.typename);if(l.typename==='LayerSet')walk(l.layers,depth+'  ');else if(l.kind===LayerKind.TEXT)texts++;}}
    walk(doc.layers,'');log('Layer objects '+count+' / editable type layers '+texts);
    var psd=new PhotoshopSaveOptions();psd.layers=true;psd.embedColorProfile=true;psd.maximizeCompatibility=true;
    doc.saveAs(new File(root.fsName+'/NPEduTools-跃迁-分层.psd'),psd,false,Extension.LOWERCASE);
    var png=new PNGSaveOptions();png.interlaced=false;
    doc.saveAs(new File(root.fsName+'/NPEduTools-跃迁-PS.png'),png,true,Extension.LOWERCASE);
    app.runMenuItem(stringIDToTypeID('fitOnScreen'));log('DONE');
  }catch(e){log('ERROR line '+e.line+': '+e);}
  finally{app.displayDialogs=oldDialogs;app.preferences.rulerUnits=oldUnits;}
})();
