// The camera and gateway are fixed. The original eight windows travel into both larger façades.
(() => {
  const section = document.querySelector('.house-film');
  const stage = section.querySelector('.house-film__stage');
  const title = stage.querySelector('h1');
  const lighting = section.querySelector('.house-film__ledger');
  const clone = id => document.getElementById(id).content.firstElementChild.cloneNode(true);
  const opener = clone('openingChapter');
  const diagram = clone('architectureChapter');
  const ledger = clone('podLedger');
  const polar = section.querySelector('.house-film__polar');
  stage.insertBefore(diagram, title);
  section.append(opener, ledger);
  const french = document.documentElement.lang === 'fr';
  if (french) {
    for (const el of document.querySelectorAll('[data-fr]')) el.textContent = el.dataset.fr;
    for (const el of document.querySelectorAll('[data-fr-label]')) el.setAttribute('aria-label', el.dataset.frLabel);
  }

  const id = name => section.querySelector('#' + name);
  const bounded = value => Math.max(0, Math.min(1, value));
  function eased(value, start, end) {
    const t = bounded((value - start) / (end - start));
    return t * t * (3 - 2 * t);
  }
  function add(parent, tag, attrs) {
    const element = document.createElementNS(diagram.namespaceURI, tag);
    for (const [key, value] of Object.entries(attrs)) element.setAttribute(key, value);
    parent.append(element);
    return element;
  }
  function path(group, d, attrs) { return add(group, 'path', {d, ...attrs}); }
  function fade(element, opacity) { element.setAttribute('opacity', opacity); }

  const houseX = [223, 347, 571, 696];
  const rooms = Array.from({length: 8}, (_, i) => [houseX[i % 4], i < 4 ? 303 : 437, 103, 94]);
  const cover = rooms.map((_, i) => add(id('coverLights'), 'rect', {
    x: [44, 132, 321, 408][i % 4], y: i < 4 ? 126 : 236, width: 55, height: 53, rx: 3
  }));
  for (let floor = 0; floor < 8; floor++) {
    path(id('towerFloors'), 'M303 ' + (224 + floor * 43) + 'h419', {});
  }
  // Mansard pavilions, a gilded central pediment and the garden axis identify the palace at a glance.
  const palaces = id('palaceBuildings');
  const gilded = {fill:'url(#gilt)', stroke:'#806b60', 'stroke-width':2};
  function pavilion(x, w, top, mansard) {
    const group = add(palaces, 'g', {transform:'translate(' + x + ' 0)'});
    path(group, 'M0 ' + top + 'h' + w + 'V560H0z', {fill:'url(#palaceWall)',stroke:'#6c7080','stroke-width':2.5});
    path(group, 'M-6 ' + top + 'l15 -' + mansard + 'h' + (w-18) + 'l15 ' + mansard + 'z', {fill:'#42536b',stroke:'#e2bd83','stroke-width':3});
    for (let xx = 15; xx < w-3; xx += 27) {
      path(group, 'M' + (xx-4) + ' ' + (top-8) + 'v-12l5-5 5 5v12z', {fill:'#829dae',stroke:'#edcd97','stroke-width':1.5});
    }
    for (const yy of [top, 366, 408, 466, 523, 555]) {
      path(group, 'M-4 ' + yy + 'h' + (w+8), {stroke:'#e1c294','stroke-width':4});
    }
    for (const xx of [3, w-8]) {
      add(group, 'rect', {x:xx,y:top+5,width:5,height:550-top,fill:'#e6d3a9'});
      path(group, 'M' + xx + ' ' + (top-mansard-1) + 'v-13', {stroke:'#edca86','stroke-width':2});
      add(group, 'circle', {cx:xx,cy:top-mansard-15,r:3,fill:'#ffdc8c'});
    }
  }
  pavilion(46, 939, 340, 29);
  for (const x of [41, 866]) pavilion(x, 125, 325, 62);
  for (const x of [234, 692]) pavilion(x, 100, 329, 42);
  pavilion(446, 139, 318, 59);
  path(palaces, 'M428 327l87-69 89 69z', gilded);
  path(palaces, 'M443 322l72-54 73 54z', {fill:'#6e7784',stroke:'#f5d394','stroke-width':3});
  add(palaces,'circle',{cx:515,cy:302,r:15,...gilded});
  path(palaces,'M515 291v11l7 4',{fill:'none',stroke:'#584d50','stroke-width':2.5});
  path(palaces,'M515 259v-47q18-8 34 2v15q-17-10-34-2',{fill:'#f4c673',stroke:'#f4ce86','stroke-width':2});
  for (const dx of [452,467,554,569]) {
    path(palaces,'M'+dx+' 348v196m-4 5h12m-12-205h12',{stroke:'#f4d7a6','stroke-width':5});
  }
  for (const x of [42,105,173,254,343,693,770,854,930,1014]) {
    add(id('palaceGardens'),'use',{href:'#topiary', transform:'translate('+x+' '+(607+Math.abs(x-515)*.04)+')'});
  }

  const panes = Array.from({length:96}, (_, i) => {
    const group = add(id('podWindows'), 'g', {});
    const frame = add(group, 'rect', {x:0,y:0,width:100,height:90,rx:6,'stroke-width':3,stroke:'#c0cdec'});
    const mullion = path(group, 'M50 1v88M2 33h96', {stroke:'#605f70','stroke-width':2.8,fill:'none'});
    const label = add(group,'text',{x:50,y:66,'text-anchor':'middle','font-size':23,'font-weight':680,fill:'#2e3852'});
    label.textContent = 'pod ' + (i+1);
    return {group, frame, mullion, label};
  });
  for (const [x,y,w,h] of rooms) {
    path(id('podConnections'),'M515 505V422H'+(x+w/2)+'V'+(y+h/2),{});
  }
  const podFields = Object.fromEntries([...ledger.querySelectorAll('[data-pod]')].map(el=>[el.dataset.pod,el]));
  const locale = french ? 'fr-FR' : 'en-IE';
  const decimalHours = new Intl.NumberFormat(locale, {minimumFractionDigits: 1, maximumFractionDigits: 1});
  const money = new Intl.NumberFormat(locale, {style:'currency',currency:'EUR',maximumFractionDigits:0});
  function towerRoom(i) {
    return [[329,402,569,642][i%4], 229+Math.floor(i/4)*43, 48, 32];
  }
  function palaceRoom(i) {
    const col = i%24;
    return [(col<12 ? 56 : 573)+(col%12)*35, 352+Math.floor(i/24)*54, 26, 39];
  }
  const mix = (a,b,t) => a.map((v,i)=>v+(b[i]-v)*t);
  const notes = french ? {
    mapping: 'Fenêtres → pods de l’application   ·   Porte → passerelle HTTP',
    bill: '8 pods × 10 € par heure = 80 € par heure',
    sleep: 'La deuxième heure sans pod ajoute 0 € à la facture.',
    growth: 'Montée en charge · compteur horaire en pause'
  } : {
    mapping: 'Windows → application pods   ·   Door → HTTP gateway',
    bill: 'Eight pods × €10 per hour = €80 per hour',
    sleep: 'Second hour at zero pods adds €0 to the bill.',
    growth: 'Scale-up snapshots · billing clock paused'
  };
  function drawArchitecture(key, p) {
    const mapping = key === 'mapping', bill = key === 'podbill', sleep = key === 'sleeping';
    const arrival = key === 'arrival', tower = key === 'tower', palace = key === 'palace';
    const towerGrowth = tower ? eased(p,.07,.87) : Number(palace);
    const wingGrowth = palace ? eased(p,.04,.85) : 0;
    const dark = sleep && p >= .22;
    const traffic = eased(p,.40,.85);
    let count = 8;
    if (mapping) count = 1 + Math.round(7*eased(p,.42,.83));
    if (dark) count = 0;
    if (arrival) count = p < .24 ? 0 : 1 + Math.round(7*traffic);
    if (tower || palace) count = 8 + Math.round(24*towerGrowth) + Math.round(64*wingGrowth);
    // Charge the first eight pods for one hour; the second hour starts once the idle timeout fires.
    // The scale-up is shown at a paused billing clock, as a series of instantaneous rates.
    const hours = mapping ? 0 : bill ? eased(p,.1,.78) : sleep ? 1+eased(p,.30,.86) : 2;
    const accrued = 80*Math.min(hours,1);
    const noteKey = mapping ? 'mapping' : bill ? 'bill' : sleep ? 'sleep' : 'growth';
    podFields.active.textContent=count;
    podFields.rate.textContent=money.format(count*10);
    podFields.elapsed.textContent=decimalHours.format(hours)+' h';
    podFields.bill.textContent=money.format(accrued);
    podFields.note.textContent=notes[noteKey];
    fade(diagram,mapping?eased(p,.03,.40):1);
    fade(id('blueprintHouse'),1-towerGrowth);
    fade(id('towerShell'),towerGrowth*(1-wingGrowth));
    fade(id('versaillesShell'),wingGrowth);
    fade(id('podConnections'),1-towerGrowth);
    const packet = id('incomingPacket');
    fade(packet,arrival && p < .25 ? 1 : 0);
    packet.setAttribute('transform','translate(515 '+(720-213*eased(p,0,.24))+')');
    for (const [i,pane] of panes.entries()) {
      const footprint = rooms[i%8];
      const towerPos = towerRoom(i%32);
      const box = mix(mix(footprint,towerPos,towerGrowth),palaceRoom(i),wingGrowth);
      const revealed = i<8 || i<count;
      pane.group.style.display=revealed?'':'none';
      if (!revealed) continue;
      const [x,y,w,h]=box;
      pane.group.setAttribute('transform','translate('+x+' '+y+') scale('+w/100+' '+h/90+')');
      // Window six was the only room lit when the resident came home.
      const rank = i===5 ? 0 : i<5 ? i+1 : i;
      const lit = rank<count;
      pane.frame.setAttribute('fill',lit?'url(#podPane)':'#283d5b');
      pane.group.dataset.lit=String(lit);
      fade(pane.mullion, lit ? .75 : .28);
      fade(pane.label, lit ? 1 - towerGrowth : 0);
    }
  }

  const ripples=id('waterRipples');
  for (let i=0; i<38; i++) {
    const x=(i*193+23)%1060, y=255+(i*31)%300;
    path(ripples,'M'+x+' '+y+'q13-3 '+(32+i%5*8)+' 0',{stroke:i%2?'#c3dce2':'#436f92','stroke-width':2.3,opacity:.5});
  }
  const icePalace = id('iceVersailles');
  const iceWindows = id('iceWindows');
  const palaceLights = [];
  // Mirror the two galleries and domed pavilions across the Sun King's entrance.
  for (const y of [250, 287]) {
    const leftWing = [427, 453, 479, 510, 537, 564, 591, 618];
    const leftPavilion = y === 250 ? [655, 690] : [655];
    for (const x of [...leftWing, ...leftPavilion]) {
      for (const center of [x, 1420 - x]) {
        add(iceWindows, 'use', {href: '#iceWindow', x: center, y});
        const glass = add(iceWindows, 'use', {href: '#litIceWindow', x: center, y, opacity: 0});
        palaceLights.push({glass, delay: (710 - x) / 320 + (y === 250 ? .14 : 0)});
      }
    }
  }
  const remainingLabel = polar.querySelector('[data-month="remaining"]');
  const savedLabel = polar.querySelector('[data-month="saved"]');
  const remainingBar = polar.querySelector('.house-film__savings-bar div');
  const monthlyBill = 10000;
  const monthlySaving = monthlyBill * .60;
  const annualTotal = polar.querySelector('[data-year="saved"]');
  const months = Array.from({length: 12}, () => {
    const month = document.createElement('span');
    month.className = 'house-film__month';
    polar.querySelector('.house-film__months').append(month);
    return month;
  });
  let savingsCue;
  let punchline;
  function drawPenguin(key, localTime, time, mouth) {
    if (key === 'savings') {
      const fraction = eased(localTime, savingsCue.start, savingsCue.end);
      const saving = Math.round(monthlySaving * fraction);
      remainingBar.style.width = (100 - 60 * fraction) + '%';
      remainingLabel.textContent = money.format(monthlyBill - saving) + (french ? ' à payer' : ' spend');
      savedLabel.textContent = money.format(saving) + (french ? ' économisés' : ' saved');
    }
    if (key === 'year') {
      const elapsed = Math.round(months.length * eased(localTime, savingsCue.start, savingsCue.end));
      annualTotal.textContent = money.format(monthlySaving * elapsed);
      months.forEach((month, i) => month.classList.toggle('house-film__month--saved', i < elapsed));
    }
    const reveal = punchline ? eased(localTime, punchline.start - 1.5, punchline.start + .12) : 0;
    fade(icePalace, reveal);
    icePalace.setAttribute('transform', 'translate(0 ' + (32 * (1 - reveal)) + ')');
    for (const {glass, delay} of palaceLights) {
      const on = punchline ? eased(localTime - punchline.start, delay - .32, delay - .10) : 0;
      fade(glass, on);
    }
    const winkTime = punchline ? localTime - punchline.end : -1;
    const wink = eased(winkTime, -.18, .04) * (1 - eased(winkTime, .38, .56));
    id('penguinWink').setAttribute('transform', 'translate(0 -237) scale(1 ' + (1 - .92 * wink) + ') translate(0 237)');
    id('floe').setAttribute('transform','translate('+(-204 * reveal)+' '+(2.8*Math.sin(time*1.4))+')');
    id('penguinBody').setAttribute('transform','rotate('+(1.25*Math.sin(time*1.9))+' 0 -12)');
    id('penguinHead').setAttribute('transform','rotate('+(2*Math.sin(time*2.2) + 6 * reveal)+' 0 -185)');
    id('penguinFlipper').setAttribute('transform','rotate('+(7+13*Math.sin(time*3.3))+' -64 -155)');
    const blink = Math.max(.10, Math.min(1,Math.abs((time+1)%3.72-.16)*9));
    id('penguinEye').setAttribute('transform','translate('+(4 * reveal)+' -237) scale(1 '+(wink > 0 ? 1 : blink)+') translate(0 237)');
    id('penguinBeak').setAttribute('transform','translate(0 '+(mouth*7)+')');
    ripples.setAttribute('transform','translate('+(3*Math.sin(time*.8))+' 0)');
  }
  window.setHouseChapter = scene => {
    const architecture=scene.visual==='architecture', penguin=scene.visual==='penguin', opening=scene.visual==='opening';
    stage.hidden=penguin||opening;
    diagram.style.display=architecture?'':'none';
    lighting.hidden=scene.visual!=='house';
    ledger.hidden=!architecture;
    opener.hidden=!opening;
    polar.hidden=!penguin;
    title.textContent=scene.headline;
    if (architecture) window.drawHouse('return',1,0);
    if (penguin) {
      punchline = scene.key === 'year' ? scene.cues.at(-1) : undefined;
      polar.querySelector('h1').textContent=scene.headline;
      if (scene.key === 'savings') savingsCue = scene.cues[1];
      if (scene.key === 'year') savingsCue = scene.cues[0];
      for (const element of polar.querySelectorAll('[data-pitch]')) element.hidden=element.dataset.pitch!==scene.key;
    }
  };
  window.drawHouseChapter = (key, visual, progress, time, filmTime, mouth) => {
    if (visual==='architecture') return drawArchitecture(key,progress);
    if (visual==='penguin') return drawPenguin(key,time,filmTime,mouth);
    if (visual==='opening') {
      cover.forEach((pane,i)=>fade(pane,.21+.79*eased(progress,.03+i*.047,.30+i*.047)));
      return;
    }
    window.drawHouse(key,progress,time);
  };
})();
