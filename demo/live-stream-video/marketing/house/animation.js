// The film renderer supplies normalized scene time, so a still and the encoded frame agree.
(() => {
  const locale = document.documentElement.lang === 'fr' ? 'fr-FR' : 'en-IE';
  const hoursFormat = new Intl.NumberFormat(locale, { minimumFractionDigits: 1, maximumFractionDigits: 1 });
  const energyFormat = new Intl.NumberFormat(locale, { minimumFractionDigits: 2, maximumFractionDigits: 2 });
  const chargeFormat = new Intl.NumberFormat(locale, { style: 'currency', currency: 'EUR' });
  const svg = document.querySelector('.house-film__skyline');
  const layer = id => svg.querySelector('#' + id);
  const clamp = value => Math.min(1, Math.max(0, value));
  const between = (t, start, end) => clamp((t - start) / (end - start));
  const smooth = t => t * t * (3 - 2 * t);
  function add(parent, tag, attrs = {}) {
    const element = document.createElementNS(svg.namespaceURI, tag);
    for (const [name, value] of Object.entries(attrs)) element.setAttribute(name, value);
    parent.append(element);
    return element;
  }
  for (let i = 0; i < 58; i++) {
    add(layer('stars'), 'circle', {cx: (157 * i + 73) % 1080, cy: (79 * i + 19) % 310,
      r: i % 6 === 0 ? 2 : 1.1, opacity: 0.3 + (i % 5) * 0.12});
  }

  const furniture = [
    '<path d="M8 80V57h24v23m9-1v-5h47v5M9 58h23" fill="currentColor"/><path d="M49 74V64q2-5 7-5h21q8 1 8 7v8z" fill="currentColor"/>',
    '<path d="M10 66h37v4H10zm5 4h3v13h-3zm26 0h3v13h-3zM67 49h22v34H67z" fill="currentColor"/><path d="M69 59h18m-18 10h18" stroke="#fff2c4" opacity=".6"/>',
    '<path d="M9 83V48h4v35zm3-14h64q12 0 12 8v6H12z" fill="currentColor"/><rect x="16" y="62" width="23" height="8" rx="4" fill="currentColor"/>',
    '<path d="M13 81V47h33v34zm44-8h34v5H57zm3 5h3v6h-3zm24 0h3v6h-3z" fill="currentColor"/><path d="M16 57h26m-26 11h26" stroke="#fff2c4" opacity=".65"/>'
  ];
  const windows = [];
  for (const y of [303, 437]) {
    for (const [column, x] of [223, 347, 571, 696].entries()) {
      const room = add(layer('rooms'), 'g', {transform: 'translate(' + x + ' ' + y + ')'});
      add(room, 'rect', {x:-4,y:-4,width:111,height:102,rx:3,fill:'#2e4157',stroke:'#adafa7','stroke-width':3});
      add(room, 'rect', {width:103,height:94,fill:'url(#coldGlass)'});
      const light = add(room, 'g');
      add(light, 'rect', {width:103,height:94,fill:'url(#roomLight)'});
      add(light, 'path', {d:'M42 23h19l31 64H12z',fill:'#fff4c2',opacity:'.27'});
      add(light, 'ellipse', {cx:52,cy:62,rx:84,ry:68,fill:'url(#aureole)'});
      const furnishing = add(room, 'g', {color:'#493f46',opacity:'.68',transform:'translate(4 5)'});
      furnishing.innerHTML = furniture[(windows.length + (y === 437 ? 1 : 0)) % furniture.length];
      add(room, 'path', {d:'M52 0v13m-11 9 5-9h12l6 9z',stroke:'#594d4f','stroke-width':2,fill:'#68514d'});
      add(light, 'ellipse', {cx:52,cy:23,rx:10,ry:3,fill:'#fff9d2'});
      add(layer('roomOpenings'), 'rect', {x,y,width:103,height:94});
      const trim = add(layer('windowTrim'), 'g', {transform:'translate(' + x + ' ' + y + ')'});
      add(trim, 'path', {d:'M0 0h12l-4 41-7 27zm103 0H91l5 42 7 23z',fill:'#756574',opacity:'.83'});
      add(trim, 'path', {d:'M0 28h103',stroke:'#677383','stroke-width':2.2});
      add(trim, 'path', {d:'M-6 95h115v6H-6z',fill:'#bbb9a9',stroke:'#4b5e70','stroke-width':1.3});
      windows.push(light);
    }
  }
  add(layer('roomOpenings'), 'path', {d:'M477 558V315a38 38 0 0 1 76 0v243z'});

  const residents = [
    {x:270,y:396,scale:1,coat:'#407780',hair:'#342e34',skin:'#e8b596',route:[[270,396],[482,396],[542,427],[486,464],[539,500]]},
    {x:409,y:530,scale:.8,coat:'#c15e4e',hair:'#4b3635',skin:'#e3ab88',route:[[409,530],[470,530]]},
    {x:748,y:397,scale:1.1,coat:'#786290',hair:'#352a30',skin:'#ce9478',route:[[748,397],[551,397],[488,426],[542,461],[494,499]]}
  ];
  const porch = [[515,538],[515,557],[526,578],[567,605],[698,633],[904,647],[1120,653]];
  function person(style, i) {
    const body = add(layer('peopleInside'), 'g');
    body.innerHTML = '<g data-pose>' +
      '<path data-arm-back fill="none" stroke="#3a475d" stroke-width="5" stroke-linecap="round"/>' +
      '<path data-leg-back fill="none" stroke="#30394b" stroke-width="5.5" stroke-linecap="round"/>' +
      '<path data-leg-front fill="none" stroke="#243145" stroke-width="6" stroke-linecap="round"/>' +
      '<path d="M-6-37Q0-43 7-36l4 20H-9z" fill="' + style.coat + '" stroke="#354252" stroke-width="1.4"/>' +
      '<path d="m3-35-2 16" stroke="#f6d9a2" opacity=".55"/>' +
      '<circle cx=".7" cy="-46" r="7.7" fill="' + style.skin + '"/>' +
      '<path d="M-7-43q-4-11 6-13 10 0 10 8l-5-2-6 2-1 7z" fill="' + style.hair + '"/>' +
      (i === 2 ? '<circle cx="-7" cy="-48" r="5" fill="' + style.hair + '"/>' : '') +
      '<path data-arm-front fill="none" stroke="' + style.skin + '" stroke-width="4.4" stroke-linecap="round"/></g>';
    return {body, pose:body.querySelector('[data-pose]'), legs:[body.querySelector('[data-leg-back]'),body.querySelector('[data-leg-front]')],
      arms:[body.querySelector('[data-arm-back]'),body.querySelector('[data-arm-front]')]};
  }
  function walk(points, progress) {
    const lengths = points.slice(1).map(([x,y],i) => Math.hypot(x-points[i][0],y-points[i][1]));
    let distance = progress * lengths.reduce((total, length) => total + length, 0);
    for (const [i,length] of lengths.entries()) {
      if (distance <= length || i === lengths.length - 1) {
        const [x,y] = points[i], [endX,endY] = points[i+1];
        const t = clamp(distance / length);
        return [x+(endX-x)*t, y+(endY-y)*t, Math.sign(endX-x) || 1];
      }
      distance -= length;
    }
  }
  const figures = residents.map(person);
  function place(i, points, progress, time) {
    const {body,pose,legs,arms} = figures[i];
    const [x,y,facing] = walk(points, progress);
    const outdoors = y >= 558;
    const parent = layer(outdoors ? 'peopleOutside' : 'peopleInside');
    if (body.parentNode !== parent) parent.append(body);
    const walking = progress > 0 && progress < 1;
    const stride = walking ? Math.sin(time * 8.5 + i * 2) : .12;
    const size = residents[i].scale * (outdoors ? 1.13 : 1.02);
    body.setAttribute('transform', 'translate(' + x + ' ' + (y - (walking ? Math.abs(stride)*1.65 : 0)) + ') scale(' + size*facing + ' ' + size + ')');
    pose.setAttribute('transform', 'rotate(' + (walking ? 2 : 0) + ' 0 -23)');
    for (let side=0; side<2; side++) {
      const swing = (side ? 1 : -1) * stride;
      const toe = swing*11;
      legs[side].setAttribute('d', 'M0-17Q' + (swing*6+2) + ' -10 ' + toe + ' -2l4 1');
      arms[side].setAttribute('d', 'M0-33l' + (-swing*5) + ' 8 ' + (-swing*4+1) + ' 6');
    }
    return clamp(1 - Math.abs(y-554)/28) * clamp(1 - Math.abs(x-515)/35);
  }

  const flowers = [[63,644,77],[85,646,104],[111,650,80],[137,651,118],[163,651,96],[191,650,73],[211,648,101]].map(([x,y,h],i) => {
    const plant = add(layer('garden'),'g',{transform:'translate(' + x + ' ' + y + ')'});
    const stalk = add(plant,'path',{fill:'none',stroke:'#7d9a81','stroke-width':4,'stroke-linecap':'round'});
    add(plant,'path',{d:'M0-19Q-22-20-23-41q17 4 23 22m0 5q21-28 33-24-4 19-33 24',fill:i%2?'#668a77':'#78947e'});
    const blossom = add(plant,'use',{href:'#flowerHead'});
    return {stalk,blossom,h,i};
  });
  const trees = [[102, 550, 1.16], [966, 560, 1.35]].map(([x, y, size], tree) => {
    const group = add(layer('gardenTrees'), 'g', {transform: 'translate('+x+' '+y+') scale('+size+')'});
    const trunk = add(group, 'use', {href: '#treeWood'});
    const leaves = Array.from({length: 65}, (_, i) => {
      // A golden-angle spiral fills the crown evenly and gives each falling leaf its own path.
      const angle = i * 2.4 + tree;
      const radius = Math.sqrt(i / 65);
      const x = 61 * radius * Math.cos(angle);
      const y = -129 + 41 * radius * Math.sin(angle) + 9 * Math.abs(x) / 61;
      const leaf = add(group, 'use', {href: '#treeLeaf'});
      return {leaf, x, y, stagger: ((i * 19 + tree * 5) % 65) / 65, angle};
    });
    return {trunk, leaves};
  });
  function drawTrees(wilt, time) {
    for (const {trunk, leaves} of trees) {
      trunk.setAttribute('color', wilt > .82 ? '#968a82' : '#826e62');
      for (const {leaf, x, y, stagger, angle} of leaves) {
        const fall = between(wilt, .08 + .57 * stagger, .39 + .57 * stagger);
        const drift = (20 + 25 * stagger) * fall + 11 * Math.sin(fall * 5 + angle) * fall;
        const height = y + (3 + 7 * stagger - y) * fall * fall;
        const turn = 20 * Math.sin(angle + time * 1.4) * (1 - fall) + fall * (100 + 160 * stagger);
        leaf.setAttribute('transform', 'translate('+(x+drift)+' '+height+') rotate('+turn+') scale('+
          (1 - .35 * fall)+' '+(1 - .74 * fall)+')');
        const green = 1 - between(wilt, .01 + .32 * stagger, .3 + .32 * stagger);
        leaf.setAttribute('fill', 'hsl('+(27+118*green)+' '+(35-8*green)+'% '+(43+8*stagger)+'%)');
      }
    }
  }
  const readings = Object.fromEntries([...document.querySelectorAll('[data-reading]')].map(el => [el.dataset.reading,el]));
  const comparison = document.querySelector('.house-film__comparison');
  window.drawHouse = (key, p, time) => {
    const replay = key === 'switch', homecoming = key === 'return';
    let door = 0;
    for (const [i, resident] of residents.entries()) {
      const body = figures[i].body;
      const path = [...resident.route, ...porch];
      body.style.display = '';
      if (homecoming && i === 1) {
        door = Math.max(door, place(i,[...porch].reverse().concat([[455,530],[409,530]]),smooth(between(p,.02,.76)),time));
      } else if (replay && i === 2) {
        door = Math.max(door, place(i,[[515,538],...porch.slice(1)],smooth(between(p,.01,.49)),time));
      } else if (key === 'evening' || key === 'leave') {
        const start = [.10,.025,.19][i], finish = [.87,.66,.98][i];
        door = Math.max(door, place(i,path,key === 'evening' ? 0 : smooth(between(p,start,finish)),time));
      } else {
        body.style.display = 'none';
      }
    }
    layer('door').setAttribute('transform','translate(483 0) scale(' + (1 - .94*smooth(clamp(door*1.9))) + ' 1) translate(-483 0)');
    const lit = windows.map((light,i) => {
      let level = 1;
      if (replay) level = 1 - smooth(between(p,.10+.014*i,.15+.014*i));
      if (homecoming) level = i === 5 ? smooth(between(p,.57,.65)) : 0;
      light.setAttribute('opacity',level);
      return level > .5 ? 1 : 0;
    }).reduce((sum,v) => sum+v,0);
    const elapsed = key === 'bill' ? between(p,.07,.86) : replay ? between(p,.34,.88) : homecoming ? 1 : 0;
    const hours = 8*elapsed;
    const kwh = key === 'bill' ? 8 * .060 * hours : 0;
    readings.lamps.textContent = lit;
    readings.hours.textContent = hoursFormat.format(hours);
    readings.energy.textContent = energyFormat.format(kwh);
    readings.charge.textContent = chargeFormat.format(.25*kwh);
    comparison.hidden = !(homecoming || (replay && p >= .87));
    const wilt = key === 'bill' ? smooth(elapsed) : 0;
    drawTrees(wilt, time);
    for (const {stalk,blossom,h,i} of flowers) {
      const dx = -5 + (i%3-1)*7 + 25*wilt + 1.8*Math.sin(time*1.8+i);
      const dy = -h + h*.47*wilt;
      stalk.setAttribute('d','M0 0Q2 ' + (-h*.73) + ' ' + (dx-6) + ' ' + (dy-11*wilt) + 'Q' + dx + ' ' + (dy-7*wilt) + ' ' + dx + ' ' + dy);
      blossom.setAttribute('transform','translate(' + dx + ' ' + dy + ') rotate(' + (i*13+73*wilt) + ') scale(' + (.85+.12*(i%3)) + ' ' + (1-.76*wilt) + ')');
      blossom.setAttribute('opacity',1-.43*wilt);
    }
  };
})();
