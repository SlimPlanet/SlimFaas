(globalThis.TURBOPACK||(globalThis.TURBOPACK=[])).push(["object"==typeof document?document.currentScript:void 0,6760,(t,e,i)=>{t.e,e.exports=function(t,e){var i=e.prototype,n=i.format;i.format=function(t){var e=this,i=this.$locale();if(!this.isValid())return n.bind(this)(t);var s=this.$utils(),r=(t||"YYYY-MM-DDTHH:mm:ssZ").replace(/\[([^\]]+)]|Q|wo|ww|w|WW|W|zzz|z|gggg|GGGG|Do|X|x|k{1,2}|S/g,function(t){switch(t){case"Q":return Math.ceil((e.$M+1)/3);case"Do":return i.ordinal(e.$D);case"gggg":return e.weekYear();case"GGGG":return e.isoWeekYear();case"wo":return i.ordinal(e.week(),"W");case"w":case"ww":return s.s(e.week(),"w"===t?1:2,"0");case"W":case"WW":return s.s(e.isoWeek(),"W"===t?1:2,"0");case"k":case"kk":return s.s(String(0===e.$H?24:e.$H),"k"===t?1:2,"0");case"X":return Math.floor(e.$d.getTime()/1e3);case"x":return e.$d.getTime();case"z":return"["+e.offsetName()+"]";case"zzz":return"["+e.offsetName("long")+"]";default:return t}});return n.bind(this)(r)}}},70574,(t,e,i)=>{t.e,e.exports=function(){"use strict";var t={LTS:"h:mm:ss A",LT:"h:mm A",L:"MM/DD/YYYY",LL:"MMMM D, YYYY",LLL:"MMMM D, YYYY h:mm A",LLLL:"dddd, MMMM D, YYYY h:mm A"},e=/(\[[^[]*\])|([-_:/.,()\s]+)|(A|a|Q|YYYY|YY?|ww?|MM?M?M?|Do|DD?|hh?|HH?|mm?|ss?|S{1,3}|z|ZZ?)/g,i=/\d/,n=/\d\d/,s=/\d\d?/,r=/\d*[^-_:/,()\s\d]+/,a={},o=function(t){return(t*=1)+(t>68?1900:2e3)},c=function(t){return function(e){this[t]=+e}},l=[/[+-]\d\d:?(\d\d)?|Z/,function(t){(this.zone||(this.zone={})).offset=function(t){if(!t||"Z"===t)return 0;var e=t.match(/([+-]|\d\d)/g),i=60*e[1]+(+e[2]||0);return 0===i?0:"+"===e[0]?-i:i}(t)}],d=function(t){var e=a[t];return e&&(e.indexOf?e:e.s.concat(e.f))},u=function(t,e){var i,n=a.meridiem;if(n){for(var s=1;s<=24;s+=1)if(t.indexOf(n(s,0,e))>-1){i=s>12;break}}else i=t===(e?"pm":"PM");return i},h={A:[r,function(t){this.afternoon=u(t,!1)}],a:[r,function(t){this.afternoon=u(t,!0)}],Q:[i,function(t){this.month=3*(t-1)+1}],S:[i,function(t){this.milliseconds=100*t}],SS:[n,function(t){this.milliseconds=10*t}],SSS:[/\d{3}/,function(t){this.milliseconds=+t}],s:[s,c("seconds")],ss:[s,c("seconds")],m:[s,c("minutes")],mm:[s,c("minutes")],H:[s,c("hours")],h:[s,c("hours")],HH:[s,c("hours")],hh:[s,c("hours")],D:[s,c("day")],DD:[n,c("day")],Do:[r,function(t){var e=a.ordinal,i=t.match(/\d+/);if(this.day=i[0],e)for(var n=1;n<=31;n+=1)e(n).replace(/\[|\]/g,"")===t&&(this.day=n)}],w:[s,c("week")],ww:[n,c("week")],M:[s,c("month")],MM:[n,c("month")],MMM:[r,function(t){var e=d("months"),i=(d("monthsShort")||e.map(function(t){return t.slice(0,3)})).indexOf(t)+1;if(i<1)throw Error();this.month=i%12||i}],MMMM:[r,function(t){var e=d("months").indexOf(t)+1;if(e<1)throw Error();this.month=e%12||e}],Y:[/[+-]?\d+/,c("year")],YY:[n,function(t){this.year=o(t)}],YYYY:[/\d{4}/,c("year")],Z:l,ZZ:l};return function(i,n,s){s.p.customParseFormat=!0,i&&i.parseTwoDigitYear&&(o=i.parseTwoDigitYear);var r=n.prototype,c=r.parse;r.parse=function(i){var n=i.date,r=i.utc,o=i.args;this.$u=r;var l=o[1];if("string"==typeof l){var d=!0===o[2],u=!0===o[3],f=o[2];u&&(f=o[2]),a=this.$locale(),!d&&f&&(a=s.Ls[f]),this.$d=function(i,n,s,r){try{if(["x","X"].indexOf(n)>-1)return new Date(("X"===n?1e3:1)*i);var o=(function(i){var n,s;n=i,s=a&&a.formats;for(var r=(i=n.replace(/(\[[^\]]+])|(LTS?|l{1,4}|L{1,4})/g,function(e,i,n){var r=n&&n.toUpperCase();return i||s[n]||t[n]||s[r].replace(/(\[[^\]]+])|(MMMM|MM|DD|dddd)/g,function(t,e,i){return e||i.slice(1)})})).match(e),o=r.length,c=0;c<o;c+=1){var l=r[c],d=h[l],u=d&&d[0],f=d&&d[1];r[c]=f?{regex:u,parser:f}:l.replace(/^\[|\]$/g,"")}return function(t){for(var e={},i=0,n=0;i<o;i+=1){var s=r[i];if("string"==typeof s)n+=s.length;else{var a=s.regex,c=s.parser,l=t.slice(n),d=a.exec(l)[0];c.call(e,d),t=t.replace(d,"")}}return function(t){var e=t.afternoon;if(void 0!==e){var i=t.hours;e?i<12&&(t.hours+=12):12===i&&(t.hours=0),delete t.afternoon}}(e),e}})(n)(i),c=o.year,l=o.month,d=o.day,u=o.hours,f=o.minutes,m=o.seconds,p=o.milliseconds,k=o.zone,y=o.week,g=new Date,v=d||(c||l?1:g.getDate()),x=c||g.getFullYear(),T=0;c&&!l||(T=l>0?l-1:g.getMonth());var b,$=u||0,w=f||0,_=m||0,D=p||0;return k?new Date(Date.UTC(x,T,v,$,w,_,D+60*k.offset*1e3)):s?new Date(Date.UTC(x,T,v,$,w,_,D)):(b=new Date(x,T,v,$,w,_,D),y&&(b=r(b).week(y).toDate()),b)}catch(t){return new Date("")}}(n,l,r,s),this.init(),f&&!0!==f&&(this.$L=this.locale(f).$L),(d||u)&&n!=this.format(l)&&(this.$d=new Date("")),a={}}else if(l instanceof Array)for(var m=l.length,p=1;p<=m;p+=1){o[1]=l[p-1];var k=s.apply(this,o);if(k.isValid()){this.$d=k.$d,this.$L=k.$L,this.init();break}p===m&&(this.$d=new Date(""))}else c.call(this,i)}}}()},98125,(t,e,i)=>{t.e,e.exports=function(){"use strict";var t,e,i=/^(-|\+)?P(?:([-+]?[0-9,.]*)Y)?(?:([-+]?[0-9,.]*)M)?(?:([-+]?[0-9,.]*)W)?(?:([-+]?[0-9,.]*)D)?(?:T(?:([-+]?[0-9,.]*)H)?(?:([-+]?[0-9,.]*)M)?(?:([-+]?[0-9,.]*)S)?)?$/,n=/\[([^\]]+)]|YYYY|YY|Y|M{1,2}|D{1,2}|H{1,2}|m{1,2}|s{1,2}|SSS/g,s={years:31536e6,months:2628e6,days:864e5,hours:36e5,minutes:6e4,seconds:1e3,milliseconds:1,weeks:6048e5},r=function(t){return t instanceof u},a=function(t,e,i){return new u(t,i,e.$l)},o=function(t){return e.p(t)+"s"},c=function(t){return t<0},l=function(t){return c(t)?Math.ceil(t):Math.floor(t)},d=function(t,e){return t?c(t)?{negative:!0,format:""+Math.abs(t)+e}:{negative:!1,format:""+t+e}:{negative:!1,format:""}},u=function(){function c(t,e,n){var r=this;if(this.$d={},this.$l=n,void 0===t&&(this.$ms=0,this.parseFromMilliseconds()),e)return a(t*s[o(e)],this);if("number"==typeof t)return this.$ms=t,this.parseFromMilliseconds(),this;if("object"==typeof t)return Object.keys(t).forEach(function(e){r.$d[o(e)]=t[e]}),this.calMilliseconds(),this;if("string"==typeof t){var c=t.match(i);if(c){var l=c.slice(2).map(function(t){return null!=t?Number(t):0});return this.$d.years=l[0],this.$d.months=l[1],this.$d.weeks=l[2],this.$d.days=l[3],this.$d.hours=l[4],this.$d.minutes=l[5],this.$d.seconds=l[6],this.calMilliseconds(),this}}return this}var u=c.prototype;return u.calMilliseconds=function(){var t=this;this.$ms=Object.keys(this.$d).reduce(function(e,i){return e+(t.$d[i]||0)*s[i]},0)},u.parseFromMilliseconds=function(){var t=this.$ms;this.$d.years=l(t/31536e6),t%=31536e6,this.$d.months=l(t/2628e6),t%=2628e6,this.$d.days=l(t/864e5),t%=864e5,this.$d.hours=l(t/36e5),t%=36e5,this.$d.minutes=l(t/6e4),t%=6e4,this.$d.seconds=l(t/1e3),t%=1e3,this.$d.milliseconds=t},u.toISOString=function(){var t=d(this.$d.years,"Y"),e=d(this.$d.months,"M"),i=+this.$d.days||0;this.$d.weeks&&(i+=7*this.$d.weeks);var n=d(i,"D"),s=d(this.$d.hours,"H"),r=d(this.$d.minutes,"M"),a=this.$d.seconds||0;this.$d.milliseconds&&(a+=this.$d.milliseconds/1e3,a=Math.round(1e3*a)/1e3);var o=d(a,"S"),c=t.negative||e.negative||n.negative||s.negative||r.negative||o.negative,l=s.format||r.format||o.format?"T":"",u=(c?"-":"")+"P"+t.format+e.format+n.format+l+s.format+r.format+o.format;return"P"===u||"-P"===u?"P0D":u},u.toJSON=function(){return this.toISOString()},u.format=function(t){var i={Y:this.$d.years,YY:e.s(this.$d.years,2,"0"),YYYY:e.s(this.$d.years,4,"0"),M:this.$d.months,MM:e.s(this.$d.months,2,"0"),D:this.$d.days,DD:e.s(this.$d.days,2,"0"),H:this.$d.hours,HH:e.s(this.$d.hours,2,"0"),m:this.$d.minutes,mm:e.s(this.$d.minutes,2,"0"),s:this.$d.seconds,ss:e.s(this.$d.seconds,2,"0"),SSS:e.s(this.$d.milliseconds,3,"0")};return(t||"YYYY-MM-DDTHH:mm:ss").replace(n,function(t,e){return e||String(i[t])})},u.as=function(t){return this.$ms/s[o(t)]},u.get=function(t){var e=this.$ms,i=o(t);return"milliseconds"===i?e%=1e3:e="weeks"===i?l(e/s[i]):this.$d[i],e||0},u.add=function(t,e,i){var n;return n=e?t*s[o(e)]:r(t)?t.$ms:a(t,this).$ms,a(this.$ms+n*(i?-1:1),this)},u.subtract=function(t,e){return this.add(t,e,!0)},u.locale=function(t){var e=this.clone();return e.$l=t,e},u.clone=function(){return a(this.$ms,this)},u.humanize=function(e){return t().add(this.$ms,"ms").locale(this.$l).fromNow(!e)},u.valueOf=function(){return this.asMilliseconds()},u.milliseconds=function(){return this.get("milliseconds")},u.asMilliseconds=function(){return this.as("milliseconds")},u.seconds=function(){return this.get("seconds")},u.asSeconds=function(){return this.as("seconds")},u.minutes=function(){return this.get("minutes")},u.asMinutes=function(){return this.as("minutes")},u.hours=function(){return this.get("hours")},u.asHours=function(){return this.as("hours")},u.days=function(){return this.get("days")},u.asDays=function(){return this.as("days")},u.weeks=function(){return this.get("weeks")},u.asWeeks=function(){return this.as("weeks")},u.months=function(){return this.get("months")},u.asMonths=function(){return this.as("months")},u.years=function(){return this.get("years")},u.asYears=function(){return this.as("years")},c}(),h=function(t,e,i){return t.add(e.years()*i,"y").add(e.months()*i,"M").add(e.days()*i,"d").add(e.hours()*i,"h").add(e.minutes()*i,"m").add(e.seconds()*i,"s").add(e.milliseconds()*i,"ms")};return function(i,n,s){t=s,e=s().$utils(),s.duration=function(t,e){return a(t,{$l:s.locale()},e)},s.isDuration=r;var o=n.prototype.add,c=n.prototype.subtract;n.prototype.add=function(t,e){return r(t)?h(this,t,1):o.bind(this)(t,e)},n.prototype.subtract=function(t,e){return r(t)?h(this,t,-1):c.bind(this)(t,e)}}}()},632,(t,e,i)=>{t.e,e.exports=function(t,e,i){var n=function(t){return t.add(4-t.isoWeekday(),"day")},s=e.prototype;s.isoWeekYear=function(){return n(this).year()},s.isoWeek=function(t){if(!this.$utils().u(t))return this.add(7*(t-this.isoWeek()),"day");var e,s,r,a=n(this),o=(e=this.isoWeekYear(),r=4-(s=(this.$u?i.utc:i)().year(e).startOf("year")).isoWeekday(),s.isoWeekday()>4&&(r+=7),s.add(r,"day"));return a.diff(o,"week")+1},s.isoWeekday=function(t){return this.$utils().u(t)?this.day()||7:this.day(this.day()%7?t:t-7)};var r=s.startOf;s.startOf=function(t,e){var i=this.$utils(),n=!!i.u(e)||e;return"isoweek"===i.p(t)?n?this.date(this.date()-(this.isoWeekday()-1)).startOf("day"):this.date(this.date()-1-(this.isoWeekday()-1)+7).endOf("day"):r.bind(this)(t,e)}}},83025,t=>{"use strict";var e,i,n,s=t.i(50574),r=t.i(73148),a=t.i(7612),o=t.i(31197),c=t.i(65167),l=t.i(82319),d=t.i(632),u=t.i(70574),h=t.i(6760),f=t.i(98125),m=t.i(30284),p=t.i(52148),k=t.i(9985),y=t.i(56240);function g(t){return t}function v(t){return"translate("+t+",0)"}function x(t){return"translate(0,"+t+")"}function T(){return!this.__axis}function b(t,e){var i=[],n=null,s=null,r=6,a=6,o=3,c="u">typeof window&&window.devicePixelRatio>1?0:.5,l=1===t||4===t?-1:1,d=4===t||2===t?"x":"y",u=1===t||3===t?v:x;function h(h){var f=null==n?e.ticks?e.ticks.apply(e,i):e.domain():n,m=null==s?e.tickFormat?e.tickFormat.apply(e,i):g:s,p=Math.max(r,0)+o,k=e.range(),y=+k[0]+c,v=+k[k.length-1]+c,x=(e.bandwidth?function(t,e){return e=Math.max(0,t.bandwidth()-2*e)/2,t.round()&&(e=Math.round(e)),i=>+t(i)+e}:function(t){return e=>+t(e)})(e.copy(),c),b=h.selection?h.selection():h,$=b.selectAll(".domain").data([null]),w=b.selectAll(".tick").data(f,e).order(),_=w.exit(),D=w.enter().append("g").attr("class","tick"),S=w.select("line"),C=w.select("text");$=$.merge($.enter().insert("path",".tick").attr("class","domain").attr("stroke","currentColor")),w=w.merge(D),S=S.merge(D.append("line").attr("stroke","currentColor").attr(d+"2",l*r)),C=C.merge(D.append("text").attr("fill","currentColor").attr(d,l*p).attr("dy",1===t?"0em":3===t?"0.71em":"0.32em")),h!==b&&($=$.transition(h),w=w.transition(h),S=S.transition(h),C=C.transition(h),_=_.transition(h).attr("opacity",1e-6).attr("transform",function(t){return isFinite(t=x(t))?u(t+c):this.getAttribute("transform")}),D.attr("opacity",1e-6).attr("transform",function(t){var e=this.parentNode.__axis;return u((e&&isFinite(e=e(t))?e:x(t))+c)})),_.remove(),$.attr("d",4===t||2===t?a?"M"+l*a+","+y+"H"+c+"V"+v+"H"+l*a:"M"+c+","+y+"V"+v:a?"M"+y+","+l*a+"V"+c+"H"+v+"V"+l*a:"M"+y+","+c+"H"+v),w.attr("opacity",1).attr("transform",function(t){return u(x(t)+c)}),S.attr(d+"2",l*r),C.attr(d,l*p).text(m),b.filter(T).attr("fill","none").attr("font-size",10).attr("font-family","sans-serif").attr("text-anchor",2===t?"start":4===t?"end":"middle"),b.each(function(){this.__axis=x})}return h.scale=function(t){return arguments.length?(e=t,h):e},h.ticks=function(){return i=Array.from(arguments),h},h.tickArguments=function(t){return arguments.length?(i=null==t?[]:Array.from(t),h):i.slice()},h.tickValues=function(t){return arguments.length?(n=null==t?null:Array.from(t),h):n&&n.slice()},h.tickFormat=function(t){return arguments.length?(s=t,h):s},h.tickSize=function(t){return arguments.length?(r=a=+t,h):r},h.tickSizeInner=function(t){return arguments.length?(r=+t,h):r},h.tickSizeOuter=function(t){return arguments.length?(a=+t,h):a},h.tickPadding=function(t){return arguments.length?(o=+t,h):o},h.offset=function(t){return arguments.length?(c=+t,h):c},h}var $=t.i(34732),w=t.i(84091),_=function(){var t=(0,o.p)(function(t,e,i,n){for(i=i||{},n=t.length;n--;i[t[n]]=e);return i},"o"),e=[6,8,10,12,13,14,15,16,17,18,20,21,22,23,24,25,26,27,28,29,30,31,33,35,36,38,40],i=[1,26],n=[1,27],s=[1,28],r=[1,29],a=[1,30],c=[1,31],l=[1,32],d=[1,33],u=[1,34],h=[1,9],f=[1,10],m=[1,11],p=[1,12],k=[1,13],y=[1,14],g=[1,15],v=[1,16],x=[1,19],T=[1,20],b=[1,21],$=[1,22],w=[1,23],_=[1,25],D=[1,35],S={trace:(0,o.p)(function(){},"trace"),yy:{},symbols_:{error:2,start:3,gantt:4,document:5,EOF:6,line:7,SPACE:8,statement:9,NL:10,weekday:11,weekday_monday:12,weekday_tuesday:13,weekday_wednesday:14,weekday_thursday:15,weekday_friday:16,weekday_saturday:17,weekday_sunday:18,weekend:19,weekend_friday:20,weekend_saturday:21,dateFormat:22,inclusiveEndDates:23,topAxis:24,axisFormat:25,tickInterval:26,excludes:27,includes:28,todayMarker:29,title:30,acc_title:31,acc_title_value:32,acc_descr:33,acc_descr_value:34,acc_descr_multiline_value:35,section:36,clickStatement:37,taskTxt:38,taskData:39,click:40,callbackname:41,callbackargs:42,href:43,clickStatementDebug:44,$accept:0,$end:1},terminals_:{2:"error",4:"gantt",6:"EOF",8:"SPACE",10:"NL",12:"weekday_monday",13:"weekday_tuesday",14:"weekday_wednesday",15:"weekday_thursday",16:"weekday_friday",17:"weekday_saturday",18:"weekday_sunday",20:"weekend_friday",21:"weekend_saturday",22:"dateFormat",23:"inclusiveEndDates",24:"topAxis",25:"axisFormat",26:"tickInterval",27:"excludes",28:"includes",29:"todayMarker",30:"title",31:"acc_title",32:"acc_title_value",33:"acc_descr",34:"acc_descr_value",35:"acc_descr_multiline_value",36:"section",38:"taskTxt",39:"taskData",40:"click",41:"callbackname",42:"callbackargs",43:"href"},productions_:[0,[3,3],[5,0],[5,2],[7,2],[7,1],[7,1],[7,1],[11,1],[11,1],[11,1],[11,1],[11,1],[11,1],[11,1],[19,1],[19,1],[9,1],[9,1],[9,1],[9,1],[9,1],[9,1],[9,1],[9,1],[9,1],[9,1],[9,1],[9,2],[9,2],[9,1],[9,1],[9,1],[9,2],[37,2],[37,3],[37,3],[37,4],[37,3],[37,4],[37,2],[44,2],[44,3],[44,3],[44,4],[44,3],[44,4],[44,2]],performAction:(0,o.p)(function(t,e,i,n,s,r,a){var o=r.length-1;switch(s){case 1:return r[o-1];case 2:case 6:case 7:this.$=[];break;case 3:r[o-1].push(r[o]),this.$=r[o-1];break;case 4:case 5:this.$=r[o];break;case 8:n.setWeekday("monday");break;case 9:n.setWeekday("tuesday");break;case 10:n.setWeekday("wednesday");break;case 11:n.setWeekday("thursday");break;case 12:n.setWeekday("friday");break;case 13:n.setWeekday("saturday");break;case 14:n.setWeekday("sunday");break;case 15:n.setWeekend("friday");break;case 16:n.setWeekend("saturday");break;case 17:n.setDateFormat(r[o].substr(11)),this.$=r[o].substr(11);break;case 18:n.enableInclusiveEndDates(),this.$=r[o].substr(18);break;case 19:n.TopAxis(),this.$=r[o].substr(8);break;case 20:n.setAxisFormat(r[o].substr(11)),this.$=r[o].substr(11);break;case 21:n.setTickInterval(r[o].substr(13)),this.$=r[o].substr(13);break;case 22:n.setExcludes(r[o].substr(9)),this.$=r[o].substr(9);break;case 23:n.setIncludes(r[o].substr(9)),this.$=r[o].substr(9);break;case 24:n.setTodayMarker(r[o].substr(12)),this.$=r[o].substr(12);break;case 27:n.setDiagramTitle(r[o].substr(6)),this.$=r[o].substr(6);break;case 28:this.$=r[o].trim(),n.setAccTitle(this.$);break;case 29:case 30:this.$=r[o].trim(),n.setAccDescription(this.$);break;case 31:n.addSection(r[o].substr(8)),this.$=r[o].substr(8);break;case 33:n.addTask(r[o-1],r[o]),this.$="task";break;case 34:this.$=r[o-1],n.setClickEvent(r[o-1],r[o],null);break;case 35:this.$=r[o-2],n.setClickEvent(r[o-2],r[o-1],r[o]);break;case 36:this.$=r[o-2],n.setClickEvent(r[o-2],r[o-1],null),n.setLink(r[o-2],r[o]);break;case 37:this.$=r[o-3],n.setClickEvent(r[o-3],r[o-2],r[o-1]),n.setLink(r[o-3],r[o]);break;case 38:this.$=r[o-2],n.setClickEvent(r[o-2],r[o],null),n.setLink(r[o-2],r[o-1]);break;case 39:this.$=r[o-3],n.setClickEvent(r[o-3],r[o-1],r[o]),n.setLink(r[o-3],r[o-2]);break;case 40:this.$=r[o-1],n.setLink(r[o-1],r[o]);break;case 41:case 47:this.$=r[o-1]+" "+r[o];break;case 42:case 43:case 45:this.$=r[o-2]+" "+r[o-1]+" "+r[o];break;case 44:case 46:this.$=r[o-3]+" "+r[o-2]+" "+r[o-1]+" "+r[o]}},"anonymous"),table:[{3:1,4:[1,2]},{1:[3]},t(e,[2,2],{5:3}),{6:[1,4],7:5,8:[1,6],9:7,10:[1,8],11:17,12:i,13:n,14:s,15:r,16:a,17:c,18:l,19:18,20:d,21:u,22:h,23:f,24:m,25:p,26:k,27:y,28:g,29:v,30:x,31:T,33:b,35:$,36:w,37:24,38:_,40:D},t(e,[2,7],{1:[2,1]}),t(e,[2,3]),{9:36,11:17,12:i,13:n,14:s,15:r,16:a,17:c,18:l,19:18,20:d,21:u,22:h,23:f,24:m,25:p,26:k,27:y,28:g,29:v,30:x,31:T,33:b,35:$,36:w,37:24,38:_,40:D},t(e,[2,5]),t(e,[2,6]),t(e,[2,17]),t(e,[2,18]),t(e,[2,19]),t(e,[2,20]),t(e,[2,21]),t(e,[2,22]),t(e,[2,23]),t(e,[2,24]),t(e,[2,25]),t(e,[2,26]),t(e,[2,27]),{32:[1,37]},{34:[1,38]},t(e,[2,30]),t(e,[2,31]),t(e,[2,32]),{39:[1,39]},t(e,[2,8]),t(e,[2,9]),t(e,[2,10]),t(e,[2,11]),t(e,[2,12]),t(e,[2,13]),t(e,[2,14]),t(e,[2,15]),t(e,[2,16]),{41:[1,40],43:[1,41]},t(e,[2,4]),t(e,[2,28]),t(e,[2,29]),t(e,[2,33]),t(e,[2,34],{42:[1,42],43:[1,43]}),t(e,[2,40],{41:[1,44]}),t(e,[2,35],{43:[1,45]}),t(e,[2,36]),t(e,[2,38],{42:[1,46]}),t(e,[2,37]),t(e,[2,39])],defaultActions:{},parseError:(0,o.p)(function(t,e){if(e.recoverable)this.trace(t);else{var i=Error(t);throw i.hash=e,i}},"parseError"),parse:(0,o.p)(function(t){var e=this,i=[0],n=[],s=[null],r=[],a=this.table,c="",l=0,d=0,u=0,h=r.slice.call(arguments,1),f=Object.create(this.lexer),m={};for(var p in this.yy)Object.prototype.hasOwnProperty.call(this.yy,p)&&(m[p]=this.yy[p]);f.setInput(t,m),m.lexer=f,m.parser=this,void 0===f.yylloc&&(f.yylloc={});var k=f.yylloc;r.push(k);var y=f.options&&f.options.ranges;function g(){var t;return"number"!=typeof(t=n.pop()||f.lex()||1)&&(t instanceof Array&&(t=(n=t).pop()),t=e.symbols_[t]||t),t}"function"==typeof m.parseError?this.parseError=m.parseError:this.parseError=Object.getPrototypeOf(this).parseError,(0,o.p)(function(t){i.length=i.length-2*t,s.length=s.length-t,r.length=r.length-t},"popStack"),(0,o.p)(g,"lex");for(var v,x,T,b,$,w,_,D,S,C={};;){if(T=i[i.length-1],this.defaultActions[T]?b=this.defaultActions[T]:(null==v&&(v=g()),b=a[T]&&a[T][v]),void 0===b||!b.length||!b[0]){var M="";for(w in S=[],a[T])this.terminals_[w]&&w>2&&S.push("'"+this.terminals_[w]+"'");M=f.showPosition?"Parse error on line "+(l+1)+":\n"+f.showPosition()+"\nExpecting "+S.join(", ")+", got '"+(this.terminals_[v]||v)+"'":"Parse error on line "+(l+1)+": Unexpected "+(1==v?"end of input":"'"+(this.terminals_[v]||v)+"'"),this.parseError(M,{text:f.match,token:this.terminals_[v]||v,line:f.yylineno,loc:k,expected:S})}if(b[0]instanceof Array&&b.length>1)throw Error("Parse Error: multiple actions possible at state: "+T+", token: "+v);switch(b[0]){case 1:i.push(v),s.push(f.yytext),r.push(f.yylloc),i.push(b[1]),v=null,x?(v=x,x=null):(d=f.yyleng,c=f.yytext,l=f.yylineno,k=f.yylloc,u>0&&u--);break;case 2:if(_=this.productions_[b[1]][1],C.$=s[s.length-_],C._$={first_line:r[r.length-(_||1)].first_line,last_line:r[r.length-1].last_line,first_column:r[r.length-(_||1)].first_column,last_column:r[r.length-1].last_column},y&&(C._$.range=[r[r.length-(_||1)].range[0],r[r.length-1].range[1]]),void 0!==($=this.performAction.apply(C,[c,d,l,m,b[1],s,r].concat(h))))return $;_&&(i=i.slice(0,-1*_*2),s=s.slice(0,-1*_),r=r.slice(0,-1*_)),i.push(this.productions_[b[1]][0]),s.push(C.$),r.push(C._$),D=a[i[i.length-2]][i[i.length-1]],i.push(D);break;case 3:return!0}}return!0},"parse")};function C(){this.yy={}}return S.lexer={EOF:1,parseError:(0,o.p)(function(t,e){if(this.yy.parser)this.yy.parser.parseError(t,e);else throw Error(t)},"parseError"),setInput:(0,o.p)(function(t,e){return this.yy=e||this.yy||{},this._input=t,this._more=this._backtrack=this.done=!1,this.yylineno=this.yyleng=0,this.yytext=this.matched=this.match="",this.conditionStack=["INITIAL"],this.yylloc={first_line:1,first_column:0,last_line:1,last_column:0},this.options.ranges&&(this.yylloc.range=[0,0]),this.offset=0,this},"setInput"),input:(0,o.p)(function(){var t=this._input[0];return this.yytext+=t,this.yyleng++,this.offset++,this.match+=t,this.matched+=t,t.match(/(?:\r\n?|\n).*/g)?(this.yylineno++,this.yylloc.last_line++):this.yylloc.last_column++,this.options.ranges&&this.yylloc.range[1]++,this._input=this._input.slice(1),t},"input"),unput:(0,o.p)(function(t){var e=t.length,i=t.split(/(?:\r\n?|\n)/g);this._input=t+this._input,this.yytext=this.yytext.substr(0,this.yytext.length-e),this.offset-=e;var n=this.match.split(/(?:\r\n?|\n)/g);this.match=this.match.substr(0,this.match.length-1),this.matched=this.matched.substr(0,this.matched.length-1),i.length-1&&(this.yylineno-=i.length-1);var s=this.yylloc.range;return this.yylloc={first_line:this.yylloc.first_line,last_line:this.yylineno+1,first_column:this.yylloc.first_column,last_column:i?(i.length===n.length?this.yylloc.first_column:0)+n[n.length-i.length].length-i[0].length:this.yylloc.first_column-e},this.options.ranges&&(this.yylloc.range=[s[0],s[0]+this.yyleng-e]),this.yyleng=this.yytext.length,this},"unput"),more:(0,o.p)(function(){return this._more=!0,this},"more"),reject:(0,o.p)(function(){return this.options.backtrack_lexer?(this._backtrack=!0,this):this.parseError("Lexical error on line "+(this.yylineno+1)+". You can only invoke reject() in the lexer when the lexer is of the backtracking persuasion (options.backtrack_lexer = true).\n"+this.showPosition(),{text:"",token:null,line:this.yylineno})},"reject"),less:(0,o.p)(function(t){this.unput(this.match.slice(t))},"less"),pastInput:(0,o.p)(function(){var t=this.matched.substr(0,this.matched.length-this.match.length);return(t.length>20?"...":"")+t.substr(-20).replace(/\n/g,"")},"pastInput"),upcomingInput:(0,o.p)(function(){var t=this.match;return t.length<20&&(t+=this._input.substr(0,20-t.length)),(t.substr(0,20)+(t.length>20?"...":"")).replace(/\n/g,"")},"upcomingInput"),showPosition:(0,o.p)(function(){var t=this.pastInput(),e=Array(t.length+1).join("-");return t+this.upcomingInput()+"\n"+e+"^"},"showPosition"),test_match:(0,o.p)(function(t,e){var i,n,s;if(this.options.backtrack_lexer&&(s={yylineno:this.yylineno,yylloc:{first_line:this.yylloc.first_line,last_line:this.last_line,first_column:this.yylloc.first_column,last_column:this.yylloc.last_column},yytext:this.yytext,match:this.match,matches:this.matches,matched:this.matched,yyleng:this.yyleng,offset:this.offset,_more:this._more,_input:this._input,yy:this.yy,conditionStack:this.conditionStack.slice(0),done:this.done},this.options.ranges&&(s.yylloc.range=this.yylloc.range.slice(0))),(n=t[0].match(/(?:\r\n?|\n).*/g))&&(this.yylineno+=n.length),this.yylloc={first_line:this.yylloc.last_line,last_line:this.yylineno+1,first_column:this.yylloc.last_column,last_column:n?n[n.length-1].length-n[n.length-1].match(/\r?\n?/)[0].length:this.yylloc.last_column+t[0].length},this.yytext+=t[0],this.match+=t[0],this.matches=t,this.yyleng=this.yytext.length,this.options.ranges&&(this.yylloc.range=[this.offset,this.offset+=this.yyleng]),this._more=!1,this._backtrack=!1,this._input=this._input.slice(t[0].length),this.matched+=t[0],i=this.performAction.call(this,this.yy,this,e,this.conditionStack[this.conditionStack.length-1]),this.done&&this._input&&(this.done=!1),i)return i;if(this._backtrack)for(var r in s)this[r]=s[r];return!1},"test_match"),next:(0,o.p)(function(){if(this.done)return this.EOF;this._input||(this.done=!0),this._more||(this.yytext="",this.match="");for(var t,e,i,n,s=this._currentRules(),r=0;r<s.length;r++)if((i=this._input.match(this.rules[s[r]]))&&(!e||i[0].length>e[0].length)){if(e=i,n=r,this.options.backtrack_lexer){if(!1!==(t=this.test_match(i,s[r])))return t;if(!this._backtrack)return!1;e=!1;continue}if(!this.options.flex)break}return e?!1!==(t=this.test_match(e,s[n]))&&t:""===this._input?this.EOF:this.parseError("Lexical error on line "+(this.yylineno+1)+". Unrecognized text.\n"+this.showPosition(),{text:"",token:null,line:this.yylineno})},"next"),lex:(0,o.p)(function(){var t=this.next();return t||this.lex()},"lex"),begin:(0,o.p)(function(t){this.conditionStack.push(t)},"begin"),popState:(0,o.p)(function(){return this.conditionStack.length-1>0?this.conditionStack.pop():this.conditionStack[0]},"popState"),_currentRules:(0,o.p)(function(){return this.conditionStack.length&&this.conditionStack[this.conditionStack.length-1]?this.conditions[this.conditionStack[this.conditionStack.length-1]].rules:this.conditions.INITIAL.rules},"_currentRules"),topState:(0,o.p)(function(t){return(t=this.conditionStack.length-1-Math.abs(t||0))>=0?this.conditionStack[t]:"INITIAL"},"topState"),pushState:(0,o.p)(function(t){this.begin(t)},"pushState"),stateStackSize:(0,o.p)(function(){return this.conditionStack.length},"stateStackSize"),options:{"case-insensitive":!0},performAction:(0,o.p)(function(t,e,i,n){switch(i){case 0:return this.begin("open_directive"),"open_directive";case 1:return this.begin("acc_title"),31;case 2:return this.popState(),"acc_title_value";case 3:return this.begin("acc_descr"),33;case 4:return this.popState(),"acc_descr_value";case 5:this.begin("acc_descr_multiline");break;case 6:case 15:case 18:case 21:case 24:this.popState();break;case 7:return"acc_descr_multiline_value";case 8:case 9:case 10:case 12:case 13:break;case 11:return 10;case 14:this.begin("href");break;case 16:return 43;case 17:this.begin("callbackname");break;case 19:this.popState(),this.begin("callbackargs");break;case 20:return 41;case 22:return 42;case 23:this.begin("click");break;case 25:return 40;case 26:return 4;case 27:return 22;case 28:return 23;case 29:return 24;case 30:return 25;case 31:return 26;case 32:return 28;case 33:return 27;case 34:return 29;case 35:return 12;case 36:return 13;case 37:return 14;case 38:return 15;case 39:return 16;case 40:return 17;case 41:return 18;case 42:return 20;case 43:return 21;case 44:return"date";case 45:return 30;case 46:return"accDescription";case 47:return 36;case 48:return 38;case 49:return 39;case 50:return":";case 51:return 6;case 52:return"INVALID"}},"anonymous"),rules:[/^(?:%%\{)/i,/^(?:accTitle\s*:\s*)/i,/^(?:(?!\n||)*[^\n]*)/i,/^(?:accDescr\s*:\s*)/i,/^(?:(?!\n||)*[^\n]*)/i,/^(?:accDescr\s*\{\s*)/i,/^(?:[\}])/i,/^(?:[^\}]*)/i,/^(?:%%(?!\{)*[^\n]*)/i,/^(?:[^\}]%%*[^\n]*)/i,/^(?:%%*[^\n]*[\n]*)/i,/^(?:[\n]+)/i,/^(?:\s+)/i,/^(?:%[^\n]*)/i,/^(?:href[\s]+["])/i,/^(?:["])/i,/^(?:[^"]*)/i,/^(?:call[\s]+)/i,/^(?:\([\s]*\))/i,/^(?:\()/i,/^(?:[^(]*)/i,/^(?:\))/i,/^(?:[^)]*)/i,/^(?:click[\s]+)/i,/^(?:[\s\n])/i,/^(?:[^\s\n]*)/i,/^(?:gantt\b)/i,/^(?:dateFormat\s[^#\n;]+)/i,/^(?:inclusiveEndDates\b)/i,/^(?:topAxis\b)/i,/^(?:axisFormat\s[^#\n;]+)/i,/^(?:tickInterval\s[^#\n;]+)/i,/^(?:includes\s[^#\n;]+)/i,/^(?:excludes\s[^#\n;]+)/i,/^(?:todayMarker\s[^\n;]+)/i,/^(?:weekday\s+monday\b)/i,/^(?:weekday\s+tuesday\b)/i,/^(?:weekday\s+wednesday\b)/i,/^(?:weekday\s+thursday\b)/i,/^(?:weekday\s+friday\b)/i,/^(?:weekday\s+saturday\b)/i,/^(?:weekday\s+sunday\b)/i,/^(?:weekend\s+friday\b)/i,/^(?:weekend\s+saturday\b)/i,/^(?:\d\d\d\d-\d\d-\d\d\b)/i,/^(?:title\s[^\n]+)/i,/^(?:accDescription\s[^#\n;]+)/i,/^(?:section\s[^\n]+)/i,/^(?:[^:\n]+)/i,/^(?::[^#\n;]+)/i,/^(?::)/i,/^(?:$)/i,/^(?:.)/i],conditions:{acc_descr_multiline:{rules:[6,7],inclusive:!1},acc_descr:{rules:[4],inclusive:!1},acc_title:{rules:[2],inclusive:!1},callbackargs:{rules:[21,22],inclusive:!1},callbackname:{rules:[18,19,20],inclusive:!1},href:{rules:[15,16],inclusive:!1},click:{rules:[24,25],inclusive:!1},INITIAL:{rules:[0,1,3,5,8,9,10,11,12,13,14,17,23,26,27,28,29,30,31,32,33,34,35,36,37,38,39,40,41,42,43,44,45,46,47,48,49,50,51,52],inclusive:!0}}},(0,o.p)(C,"Parser"),C.prototype=S,S.Parser=C,new C}();_.parser=_,l.default.extend(d.default),l.default.extend(u.default),l.default.extend(h.default);var D={friday:5,saturday:6},S="",C="",M=void 0,Y="",E=[],O=[],A=new Map,L=[],I=[],F="",W="",P=["active","done","crit","milestone","vert"],z=[],H="",B=!1,N=!1,R="sunday",j="saturday",G=0,V=(0,o.p)(function(){L=[],I=[],F="",z=[],tD=0,e=void 0,i=void 0,tY=[],S="",C="",W="",M=void 0,Y="",E=[],O=[],B=!1,N=!1,G=0,A=new Map,H="",(0,r.M)(),R="sunday",j="saturday"},"clear"),U=(0,o.p)(function(t){H=t},"setDiagramId"),Z=(0,o.p)(function(t){C=t},"setAxisFormat"),X=(0,o.p)(function(){return C},"getAxisFormat"),q=(0,o.p)(function(t){M=t},"setTickInterval"),Q=(0,o.p)(function(){return M},"getTickInterval"),K=(0,o.p)(function(t){Y=t},"setTodayMarker"),J=(0,o.p)(function(){return Y},"getTodayMarker"),tt=(0,o.p)(function(t){S=t},"setDateFormat"),te=(0,o.p)(function(){B=!0},"enableInclusiveEndDates"),ti=(0,o.p)(function(){return B},"endDatesAreInclusive"),tn=(0,o.p)(function(){N=!0},"enableTopAxis"),ts=(0,o.p)(function(){return N},"topAxisEnabled"),tr=(0,o.p)(function(t){W=t},"setDisplayMode"),ta=(0,o.p)(function(){return W},"getDisplayMode"),to=(0,o.p)(function(){return S},"getDateFormat"),tc=(0,o.p)((t,e)=>[...new Set([...t,...e.toLowerCase().split(/[\s,]+/).filter(t=>""!==t)])],"mergeTokens"),tl=(0,o.p)(function(t){E=tc(E,t)},"setIncludes"),td=(0,o.p)(function(){return E},"getIncludes"),tu=(0,o.p)(function(t){O=tc(O,t)},"setExcludes"),th=(0,o.p)(function(){return O},"getExcludes"),tf=(0,o.p)(function(){return A},"getLinks"),tm=(0,o.p)(function(t){F=t,L.push(t)},"addSection"),tp=(0,o.p)(function(){return L},"getSections"),tk=(0,o.p)(function(){let t=tI(),e=0;for(;!t&&e<10;)t=tI(),e++;return I=tY},"getTasks"),ty=(0,o.p)(function(t,e,i,n){let s=t.format(e.trim()),r=t.format("YYYY-MM-DD");return!(n.includes(s)||n.includes(r))&&(!!(i.includes("weekends")&&(t.isoWeekday()===D[j]||t.isoWeekday()===D[j]+1)||i.includes(t.format("dddd").toLowerCase()))||i.includes(s)||i.includes(r))},"isInvalidDate"),tg=(0,o.p)(function(t){R=t},"setWeekday"),tv=(0,o.p)(function(){return R},"getWeekday"),tx=(0,o.p)(function(t){j=t},"setWeekend"),tT=(0,o.p)(function(t,e,i,n){let s;if(!i.length||t.manualEndTime)return;let[r,a]=tb(s=(s=t.startTime instanceof Date?(0,l.default)(t.startTime):(0,l.default)(t.startTime,e,!0)).add(1,"d"),t.endTime instanceof Date?(0,l.default)(t.endTime):(0,l.default)(t.endTime,e,!0),e,i,n);t.endTime=r.toDate(),t.renderEndTime=a},"checkTaskDates"),tb=(0,o.p)(function(t,e,i,n,s){let r=!1,a=null,o=e.add(1e4,"d");for(;t<=e;){if(r||(a=e.toDate()),(r=ty(t,i,n,s))&&(e=e.add(1,"d"))>o)throw Error("Failed to find a valid date that was not excluded by `excludes` after 10,000 iterations.");t=t.add(1,"d")}return[e,a]},"fixTaskDates"),t$=(0,o.p)(function(t,e,i){if(i=i.trim(),(0,o.p)(t=>{let e=t.trim();return"x"===e||"X"===e},"isTimestampFormat")(e)&&/^\d+$/.test(i))return new Date(Number(i));let n=/^after\s+(?<ids>[\d\w- ]+)/.exec(i);if(null!==n){let t=null;for(let e of n.groups.ids.split(" ")){let i=tA(e);void 0!==i&&(!t||i.endTime>t.endTime)&&(t=i)}if(t)return t.endTime;let e=new Date;return e.setHours(0,0,0,0),e}let s=(0,l.default)(i,e.trim(),!0);if(s.isValid())return s.toDate();{a.d.debug("Invalid date:"+i),a.d.debug("With date format:"+e.trim());let t=new Date(i);if(void 0===t||isNaN(t.getTime())||-1e4>t.getFullYear()||t.getFullYear()>1e4)throw Error("Invalid date:"+i);return t}},"getStartDate"),tw=(0,o.p)(function(t){let e=/^(\d+(?:\.\d+)?)([Mdhmswy]|ms)$/.exec(t.trim());return null!==e?[Number.parseFloat(e[1]),e[2]]:[0/0,"ms"]},"parseDuration"),t_=(0,o.p)(function(t,e,i,n=!1){i=i.trim();let s=/^until\s+(?<ids>[\d\w- ]+)/.exec(i);if(null!==s){let t=null;for(let e of s.groups.ids.split(" ")){let i=tA(e);void 0!==i&&(!t||i.startTime<t.startTime)&&(t=i)}if(t)return t.startTime;let e=new Date;return e.setHours(0,0,0,0),e}let r=(0,l.default)(i,e.trim(),!0);if(r.isValid())return n&&(r=r.add(1,"d")),r.toDate();let a=(0,l.default)(t),[o,c]=tw(i);if(!Number.isNaN(o)){let t=a.add(o,c);t.isValid()&&(a=t)}return a.toDate()},"getEndDate"),tD=0,tS=(0,o.p)(function(t){return void 0===t?"task"+(tD+=1):t},"parseId"),tC=(0,o.p)(function(t,e){let i=(":"===e.substr(0,1)?e.substr(1,e.length):e).split(","),n={};tR(i,n,P);for(let t=0;t<i.length;t++)i[t]=i[t].trim();let s="";switch(i.length){case 1:n.id=tS(),n.startTime=t.endTime,s=i[0];break;case 2:n.id=tS(),n.startTime=t$(void 0,S,i[0]),s=i[1];break;case 3:n.id=tS(i[0]),n.startTime=t$(void 0,S,i[1]),s=i[2]}return s&&(n.endTime=t_(n.startTime,S,s,B),n.manualEndTime=(0,l.default)(s,"YYYY-MM-DD",!0).isValid(),tT(n,S,O,E)),n},"compileData"),tM=(0,o.p)(function(t,e){let i=(":"===e.substr(0,1)?e.substr(1,e.length):e).split(","),n={};tR(i,n,P);for(let t=0;t<i.length;t++)i[t]=i[t].trim();switch(i.length){case 1:n.id=tS(),n.startTime={type:"prevTaskEnd",id:t},n.endTime={data:i[0]};break;case 2:n.id=tS(),n.startTime={type:"getStartDate",startData:i[0]},n.endTime={data:i[1]};break;case 3:n.id=tS(i[0]),n.startTime={type:"getStartDate",startData:i[1]},n.endTime={data:i[2]}}return n},"parseData"),tY=[],tE={},tO=(0,o.p)(function(t,e){let n={section:F,type:F,processed:!1,manualEndTime:!1,renderEndTime:null,raw:{data:e},task:t,classes:[]},s=tM(i,e);n.raw.startTime=s.startTime,n.raw.endTime=s.endTime,n.id=s.id,n.prevTaskId=i,n.active=s.active,n.done=s.done,n.crit=s.crit,n.milestone=s.milestone,n.vert=s.vert,n.vert?n.order=-1:(n.order=G,G++);let r=tY.push(n);i=n.id,tE[n.id]=r-1},"addTask"),tA=(0,o.p)(function(t){return tY[tE[t]]},"findTaskById"),tL=(0,o.p)(function(t,i){let n={section:F,type:F,description:t,task:t,classes:[]},s=tC(e,i);n.startTime=s.startTime,n.endTime=s.endTime,n.id=s.id,n.active=s.active,n.done=s.done,n.crit=s.crit,n.milestone=s.milestone,n.vert=s.vert,e=n,I.push(n)},"addTaskOrg"),tI=(0,o.p)(function(){let t=(0,o.p)(function(t){let e=tY[t],i="";switch(tY[t].raw.startTime.type){case"prevTaskEnd":{let t=tA(e.prevTaskId);e.startTime=t.endTime;break}case"getStartDate":(i=t$(void 0,S,tY[t].raw.startTime.startData))&&(tY[t].startTime=i)}return tY[t].startTime&&(tY[t].endTime=t_(tY[t].startTime,S,tY[t].raw.endTime.data,B),tY[t].endTime&&(tY[t].processed=!0,tY[t].manualEndTime=(0,l.default)(tY[t].raw.endTime.data,"YYYY-MM-DD",!0).isValid(),tT(tY[t],S,O,E))),tY[t].processed},"compileTask"),e=!0;for(let[i,n]of tY.entries())t(i),e=e&&n.processed;return e},"compileTasks"),tF=(0,o.p)(function(t,e){let i=e;"loose"!==(0,r.z)().securityLevel&&(i=(0,c.sanitizeUrl)(e)),t.split(",").forEach(function(t){void 0!==tA(t)&&(tz(t,()=>{window.open(i,"_self")}),A.set(t,i))}),tW(t,"clickable")},"setLink"),tW=(0,o.p)(function(t,e){t.split(",").forEach(function(t){let i=tA(t);void 0!==i&&i.classes.push(e)})},"setClass"),tP=(0,o.p)(function(t,e,i){if("loose"!==(0,r.z)().securityLevel||void 0===e)return;let n=[];if("string"==typeof i){n=i.split(/,(?=(?:(?:[^"]*"){2})*[^"]*$)/);for(let t=0;t<n.length;t++){let e=n[t].trim();e.startsWith('"')&&e.endsWith('"')&&(e=e.substr(1,e.length-2)),n[t]=e}}0===n.length&&n.push(t),void 0!==tA(t)&&tz(t,()=>{s.u.runFunc(e,...n)})},"setClickFun"),tz=(0,o.p)(function(t,e){z.push(function(){let i=H?`${H}-${t}`:t,n=document.querySelector(`[id="${i}"]`);null!==n&&n.addEventListener("click",function(){e()})},function(){let i=H?`${H}-${t}`:t,n=document.querySelector(`[id="${i}-text"]`);null!==n&&n.addEventListener("click",function(){e()})})},"pushFun"),tH=(0,o.p)(function(t,e,i){t.split(",").forEach(function(t){tP(t,e,i)}),tW(t,"clickable")},"setClickEvent"),tB=(0,o.p)(function(t){z.forEach(function(e){e(t)})},"bindFunctions"),tN={getConfig:(0,o.p)(()=>(0,r.z)().gantt,"getConfig"),clear:V,setDateFormat:tt,getDateFormat:to,enableInclusiveEndDates:te,endDatesAreInclusive:ti,enableTopAxis:tn,topAxisEnabled:ts,setAxisFormat:Z,getAxisFormat:X,setTickInterval:q,getTickInterval:Q,setTodayMarker:K,getTodayMarker:J,setAccTitle:r.b,getAccTitle:r.d,setDiagramTitle:r.a,getDiagramTitle:r.F,setDiagramId:U,setDisplayMode:tr,getDisplayMode:ta,setAccDescription:r.C,getAccDescription:r.j,addSection:tm,getSections:tp,getTasks:tk,addTask:tO,findTaskById:tA,addTaskOrg:tL,setIncludes:tl,getIncludes:td,setExcludes:tu,getExcludes:th,setClickEvent:tH,setLink:tF,getLinks:tf,bindFunctions:tB,parseDuration:tw,isInvalidDate:ty,setWeekday:tg,getWeekday:tv,setWeekend:tx};function tR(t,e,i){let n=!0;for(;n;)n=!1,i.forEach(function(i){let s=RegExp("^\\s*"+i+"\\s*$");t[0].match(s)&&(e[i]=!0,t.shift(1),n=!0)})}(0,o.p)(tR,"getTaskTags"),l.default.extend(f.default);var tj=(0,o.p)(function(){a.d.debug("Something is calling, setConf, remove the call")},"setConf"),tG={monday:w.j,tuesday:w.K,wednesday:w.O,thursday:w.R,friday:w.Q,saturday:w.v,sunday:w.w},tV=(0,o.p)((t,e)=>{let i=[...t].map(()=>-1/0),n=[...t].sort((t,e)=>t.startTime-e.startTime||t.order-e.order),s=0;for(let t of n)for(let n=0;n<i.length;n++)if(t.startTime>=i[n]){i[n]=t.endTime,t.order=n+e,n>s&&(s=n);break}return s},"getMaxIntersections"),tU=(0,o.p)(function(t,e,i,s){let c,d=(0,r.z)().gantt;s.db.setDiagramId(e);let u=(0,r.z)().securityLevel;"sandbox"===u&&(c=(0,m.f)("#i"+e));let h="sandbox"===u?(0,m.f)(c.nodes()[0].contentDocument.body):(0,m.f)("body"),f="sandbox"===u?c.nodes()[0].contentDocument:document,g=f.getElementById(e);void 0===(n=g.parentElement.offsetWidth)&&(n=1200),void 0!==d.useWidth&&(n=d.useWidth);let v=s.db.getTasks(),x=v.filter(t=>!t.vert),T=[];for(let t of x)T.push(t.type);T=W(T);let _={},D=2*d.topPadding;if("compact"===s.db.getDisplayMode()||"compact"===d.displayMode){let t={};for(let e of x)void 0===t[e.section]?t[e.section]=[e]:t[e.section].push(e);let e=0;for(let i of Object.keys(t)){let n=tV(t[i],e)+1;e+=n,D+=n*(d.barHeight+d.barGap),_[i]=n}}else for(let t of(D+=x.length*(d.barHeight+d.barGap),T))_[t]=x.filter(e=>e.type===t).length;g.setAttribute("viewBox","0 0 "+n+" "+D);let S=h.select(`[id="${e}"]`),C=(0,p.g)().domain([(0,k.t)(v,function(t){return t.startTime}),(0,k.y)(v,function(t){return t.endTime})]).rangeRound([0,n-d.leftPadding-d.rightPadding]);function M(t,e){let i=t.startTime,n=e.startTime,s=0;return i>n?s=1:i<n&&(s=-1),s}function Y(t,e,i){let n=d.barHeight,r=n+d.barGap,a=d.topPadding,o=d.leftPadding,c=(0,p.t)().domain([0,T.length]).range(["#00B9FA","#F95002"]).interpolate(y.K);O(r,a,o,e,i,t,s.db.getExcludes(),s.db.getIncludes()),L(o,a,e,i),E(t,r,a,o,n,c,e),I(r,a),F(o,a,e,i)}function E(t,i,n,a,o,c,l){t.sort((t,e)=>t.vert===e.vert?0:t.vert?1:-1);let u=t.filter(t=>!t.vert),h=[...new Set(u.map(t=>t.order))].map(t=>u.find(e=>e.order===t));S.append("g").selectAll("rect").data(h).enter().append("rect").attr("x",0).attr("y",function(t,e){return t.order*i+n-2}).attr("width",function(){return l-d.rightPadding/2}).attr("height",i).attr("class",function(t){for(let[e,i]of T.entries())if(t.type===i)return"section section"+e%d.numberSectionStyles;return"section section0"}).enter();let f=S.append("g").selectAll("rect").data(t).enter(),p=s.db.getLinks();if(f.append("rect").attr("id",function(t){return e+"-"+t.id}).attr("rx",3).attr("ry",3).attr("x",function(t){return t.milestone?C(t.startTime)+a+.5*(C(t.endTime)-C(t.startTime))-.5*o:C(t.startTime)+a}).attr("y",function(t,e){return(e=t.order,t.vert)?d.gridLineStartPadding:e*i+n}).attr("width",function(t){return t.milestone?o:t.vert?.08*o:C(t.renderEndTime||t.endTime)-C(t.startTime)}).attr("height",function(t){return t.vert?u.length*(d.barHeight+d.barGap)+2*d.barHeight:o}).attr("transform-origin",function(t,e){return e=t.order,(C(t.startTime)+a+.5*(C(t.endTime)-C(t.startTime))).toString()+"px "+(e*i+n+.5*o).toString()+"px"}).attr("class",function(t){let e="";t.classes.length>0&&(e=t.classes.join(" "));let i=0;for(let[e,n]of T.entries())t.type===n&&(i=e%d.numberSectionStyles);let n="";return t.active?t.crit?n+=" activeCrit":n=" active":t.done?n=t.crit?" doneCrit":" done":t.crit&&(n+=" crit"),0===n.length&&(n=" task"),t.milestone&&(n=" milestone "+n),t.vert&&(n=" vert "+n),n+=i,"task"+(n+=" "+e)}),f.append("text").attr("id",function(t){return e+"-"+t.id+"-text"}).text(function(t){return t.task}).attr("font-size",d.fontSize).attr("x",function(t){let e=C(t.startTime),i=C(t.renderEndTime||t.endTime);if(t.milestone&&(e+=.5*(C(t.endTime)-C(t.startTime))-.5*o,i=e+o),t.vert)return C(t.startTime)+a;let n=this.getBBox().width;return n>i-e?i+n+1.5*d.leftPadding>l?e+a-5:i+a+5:(i-e)/2+e+a}).attr("y",function(t,e){return t.vert?d.gridLineStartPadding+u.length*(d.barHeight+d.barGap)+60:t.order*i+d.barHeight/2+(d.fontSize/2-2)+n}).attr("text-height",o).attr("class",function(t){let e=C(t.startTime),i=C(t.endTime);t.milestone&&(i=e+o);let n=this.getBBox().width,s="";t.classes.length>0&&(s=t.classes.join(" "));let r=0;for(let[e,i]of T.entries())t.type===i&&(r=e%d.numberSectionStyles);let a="";return(t.active&&(a=t.crit?"activeCritText"+r:"activeText"+r),t.done?a=t.crit?a+" doneCritText"+r:a+" doneText"+r:t.crit&&(a=a+" critText"+r),t.milestone&&(a+=" milestoneText"),t.vert&&(a+=" vertText"),n>i-e)?i+n+1.5*d.leftPadding>l?s+" taskTextOutsideLeft taskTextOutside"+r+" "+a:s+" taskTextOutsideRight taskTextOutside"+r+" "+a+" width-"+n:s+" taskText taskText"+r+" "+a+" width-"+n}),"sandbox"===(0,r.z)().securityLevel){let t=(0,m.f)("#i"+e).nodes()[0].contentDocument;f.filter(function(t){return p.has(t.id)}).each(function(i){var n=t.querySelector("#"+CSS.escape(e+"-"+i.id)),s=t.querySelector("#"+CSS.escape(e+"-"+i.id+"-text"));let r=n.parentNode;var a=t.createElement("a");a.setAttribute("xlink:href",p.get(i.id)),a.setAttribute("target","_top"),r.appendChild(a),a.appendChild(n),a.appendChild(s)})}}function O(t,i,n,r,o,c,u,h){let f,m;if(0===u.length&&0===h.length)return;for(let{startTime:t,endTime:e}of c)(void 0===f||t<f)&&(f=t),(void 0===m||e>m)&&(m=e);if(!f||!m)return;if((0,l.default)(m).diff((0,l.default)(f),"year")>5)return void a.d.warn("The difference between the min and max time is more than 5 years. This will cause performance issues. Skipping drawing exclude days.");let p=s.db.getDateFormat(),k=[],y=null,g=(0,l.default)(f);for(;g.valueOf()<=m;)s.db.isInvalidDate(g,p,u,h)?y?y.end=g:y={start:g,end:g}:y&&(k.push(y),y=null),g=g.add(1,"d");S.append("g").selectAll("rect").data(k).enter().append("rect").attr("id",t=>e+"-exclude-"+t.start.format("YYYY-MM-DD")).attr("x",t=>C(t.start.startOf("day"))+n).attr("y",d.gridLineStartPadding).attr("width",t=>C(t.end.endOf("day"))-C(t.start.startOf("day"))).attr("height",o-i-d.gridLineStartPadding).attr("transform-origin",function(e,i){return(C(e.start)+n+.5*(C(e.end)-C(e.start))).toString()+"px "+(i*t+.5*o).toString()+"px"}).attr("class","exclude-range")}function A(t,e,i,n){if(i<=0||t>e)return 1/0;let s=l.default.duration({[n??"day"]:i}).asMilliseconds();return s<=0?1/0:Math.ceil((e-t)/s)}function L(t,e,i,n){let r,o=s.db.getDateFormat(),c=s.db.getAxisFormat();r=c||("D"===o?"%d":d.axisFormat??"%Y-%m-%d");let l=b(3,C).tickSize(-n+e+d.gridLineStartPadding).tickFormat((0,$.r)(r)),u=/^([1-9]\d*)(millisecond|second|minute|hour|day|week|month)$/.exec(s.db.getTickInterval()||d.tickInterval);if(null!==u){let t=parseInt(u[1],10);if(isNaN(t)||t<=0)a.d.warn(`Invalid tick interval value: "${u[1]}". Skipping custom tick interval.`);else{let e=u[2],i=s.db.getWeekday()||d.weekday,n=C.domain(),r=A(n[0],n[1],t,e);if(r>1e4)a.d.warn(`The tick interval "${t}${e}" would generate ${r} ticks, which exceeds the maximum allowed (10000). This may indicate an invalid date or time range. Skipping custom tick interval.`);else switch(e){case"millisecond":l.ticks(w.p.every(t));break;case"second":l.ticks(w.A.every(t));break;case"minute":l.ticks(w.e.every(t));break;case"hour":l.ticks(w.U.every(t));break;case"day":l.ticks(w.X.every(t));break;case"week":l.ticks(tG[i].every(t));break;case"month":l.ticks(w.P.every(t))}}}if(S.append("g").attr("class","grid").attr("transform","translate("+t+", "+(n-50)+")").call(l).selectAll("text").style("text-anchor","middle").attr("fill","#000").attr("stroke","none").attr("font-size",10).attr("dy","1em"),s.db.topAxisEnabled()||d.topAxis){let i=b(1,C).tickSize(-n+e+d.gridLineStartPadding).tickFormat((0,$.r)(r));if(null!==u){let t=parseInt(u[1],10);if(isNaN(t)||t<=0)a.d.warn(`Invalid tick interval value: "${u[1]}". Skipping custom tick interval.`);else{let e=u[2],n=s.db.getWeekday()||d.weekday,r=C.domain();if(1e4>=A(r[0],r[1],t,e))switch(e){case"millisecond":i.ticks(w.p.every(t));break;case"second":i.ticks(w.A.every(t));break;case"minute":i.ticks(w.e.every(t));break;case"hour":i.ticks(w.U.every(t));break;case"day":i.ticks(w.X.every(t));break;case"week":i.ticks(tG[n].every(t));break;case"month":i.ticks(w.P.every(t))}}}S.append("g").attr("class","grid").attr("transform","translate("+t+", "+e+")").call(i).selectAll("text").style("text-anchor","middle").attr("fill","#000").attr("stroke","none").attr("font-size",10)}}function I(t,e){let i=0,n=Object.keys(_).map(t=>[t,_[t]]);S.append("g").selectAll("text").data(n).enter().append(function(t){let e=t[0].split(r.E.lineBreakRegex),i=-(e.length-1)/2,n=f.createElementNS("http://www.w3.org/2000/svg","text");for(let[t,s]of(n.setAttribute("dy",i+"em"),e.entries())){let e=f.createElementNS("http://www.w3.org/2000/svg","tspan");e.setAttribute("alignment-baseline","central"),e.setAttribute("x","10"),t>0&&e.setAttribute("dy","1em"),e.textContent=s,n.appendChild(e)}return n}).attr("x",10).attr("y",function(s,r){if(!(r>0))return s[1]*t/2+e;for(let a=0;a<r;a++)return i+=n[r-1][1],s[1]*t/2+i*t+e}).attr("font-size",d.sectionFontSize).attr("class",function(t){for(let[e,i]of T.entries())if(t[0]===i)return"sectionTitle sectionTitle"+e%d.numberSectionStyles;return"sectionTitle"})}function F(t,e,i,n){let r=s.db.getTodayMarker();if("off"===r)return;let a=S.append("g").attr("class","today"),o=new Date,c=a.append("line");c.attr("x1",C(o)+t).attr("x2",C(o)+t).attr("y1",d.titleTopMargin).attr("y2",n-d.titleTopMargin).attr("class","today"),""!==r&&c.attr("style",r.replace(/,/g,";"))}function W(t){let e={},i=[];for(let n=0,s=t.length;n<s;++n)Object.prototype.hasOwnProperty.call(e,t[n])||(e[t[n]]=!0,i.push(t[n]));return i}(0,o.p)(M,"taskCompare"),v.sort(M),Y(v,n,D),(0,r.m)(S,D,n,d.useMaxWidth),S.append("text").text(s.db.getDiagramTitle()).attr("x",n/2).attr("y",d.titleTopMargin).attr("class","titleText"),(0,o.p)(Y,"makeGantt"),(0,o.p)(E,"drawRects"),(0,o.p)(O,"drawExcludeDays"),(0,o.p)(A,"getEstimatedTickCount"),(0,o.p)(L,"makeGrid"),(0,o.p)(I,"vertLabels"),(0,o.p)(F,"drawToday"),(0,o.p)(W,"checkUnique")},"draw"),tZ=(0,o.p)(t=>`
  .mermaid-main-font {
        font-family: ${t.fontFamily};
  }

  .exclude-range {
    fill: ${t.excludeBkgColor};
  }

  .section {
    stroke: none;
    opacity: 0.2;
  }

  .section0 {
    fill: ${t.sectionBkgColor};
  }

  .section2 {
    fill: ${t.sectionBkgColor2};
  }

  .section1,
  .section3 {
    fill: ${t.altSectionBkgColor};
    opacity: 0.2;
  }

  .sectionTitle0 {
    fill: ${t.titleColor};
  }

  .sectionTitle1 {
    fill: ${t.titleColor};
  }

  .sectionTitle2 {
    fill: ${t.titleColor};
  }

  .sectionTitle3 {
    fill: ${t.titleColor};
  }

  .sectionTitle {
    text-anchor: start;
    font-family: ${t.fontFamily};
  }


  /* Grid and axis */

  .grid .tick {
    stroke: ${t.gridColor};
    opacity: 0.8;
    shape-rendering: crispEdges;
  }

  .grid .tick text {
    font-family: ${t.fontFamily};
    fill: ${t.textColor};
  }

  .grid path {
    stroke-width: 0;
  }


  /* Today line */

  .today {
    fill: none;
    stroke: ${t.todayLineColor};
    stroke-width: 2px;
  }


  /* Task styling */

  /* Default task */

  .task {
    stroke-width: 2;
  }

  .taskText {
    text-anchor: middle;
    font-family: ${t.fontFamily};
  }

  .taskTextOutsideRight {
    fill: ${t.taskTextDarkColor};
    text-anchor: start;
    font-family: ${t.fontFamily};
  }

  .taskTextOutsideLeft {
    fill: ${t.taskTextDarkColor};
    text-anchor: end;
  }


  /* Special case clickable */

  .task.clickable {
    cursor: pointer;
  }

  .taskText.clickable {
    cursor: pointer;
    fill: ${t.taskTextClickableColor} !important;
    font-weight: bold;
  }

  .taskTextOutsideLeft.clickable {
    cursor: pointer;
    fill: ${t.taskTextClickableColor} !important;
    font-weight: bold;
  }

  .taskTextOutsideRight.clickable {
    cursor: pointer;
    fill: ${t.taskTextClickableColor} !important;
    font-weight: bold;
  }


  /* Specific task settings for the sections*/

  .taskText0,
  .taskText1,
  .taskText2,
  .taskText3 {
    fill: ${t.taskTextColor};
  }

  .task0,
  .task1,
  .task2,
  .task3 {
    fill: ${t.taskBkgColor};
    stroke: ${t.taskBorderColor};
  }

  .taskTextOutside0,
  .taskTextOutside2
  {
    fill: ${t.taskTextOutsideColor};
  }

  .taskTextOutside1,
  .taskTextOutside3 {
    fill: ${t.taskTextOutsideColor};
  }


  /* Active task */

  .active0,
  .active1,
  .active2,
  .active3 {
    fill: ${t.activeTaskBkgColor};
    stroke: ${t.activeTaskBorderColor};
  }

  .activeText0,
  .activeText1,
  .activeText2,
  .activeText3 {
    fill: ${t.taskTextDarkColor} !important;
  }


  /* Completed task */

  .done0,
  .done1,
  .done2,
  .done3 {
    stroke: ${t.doneTaskBorderColor};
    fill: ${t.doneTaskBkgColor};
    stroke-width: 2;
  }

  .doneText0,
  .doneText1,
  .doneText2,
  .doneText3 {
    fill: ${t.taskTextDarkColor} !important;
  }

  /* Done task text displayed outside the bar sits against the diagram background,
     not against the done-task bar, so it must use the outside/contrast color. */
  .doneText0.taskTextOutsideLeft,
  .doneText0.taskTextOutsideRight,
  .doneText1.taskTextOutsideLeft,
  .doneText1.taskTextOutsideRight,
  .doneText2.taskTextOutsideLeft,
  .doneText2.taskTextOutsideRight,
  .doneText3.taskTextOutsideLeft,
  .doneText3.taskTextOutsideRight {
    fill: ${t.taskTextOutsideColor} !important;
  }


  /* Tasks on the critical line */

  .crit0,
  .crit1,
  .crit2,
  .crit3 {
    stroke: ${t.critBorderColor};
    fill: ${t.critBkgColor};
    stroke-width: 2;
  }

  .activeCrit0,
  .activeCrit1,
  .activeCrit2,
  .activeCrit3 {
    stroke: ${t.critBorderColor};
    fill: ${t.activeTaskBkgColor};
    stroke-width: 2;
  }

  .doneCrit0,
  .doneCrit1,
  .doneCrit2,
  .doneCrit3 {
    stroke: ${t.critBorderColor};
    fill: ${t.doneTaskBkgColor};
    stroke-width: 2;
    cursor: pointer;
    shape-rendering: crispEdges;
  }

  .milestone {
    transform: rotate(45deg) scale(0.8,0.8);
  }

  .milestoneText {
    font-style: italic;
  }
  .doneCritText0,
  .doneCritText1,
  .doneCritText2,
  .doneCritText3 {
    fill: ${t.taskTextDarkColor} !important;
  }

  /* Done-crit task text outside the bar — same reasoning as doneText above. */
  .doneCritText0.taskTextOutsideLeft,
  .doneCritText0.taskTextOutsideRight,
  .doneCritText1.taskTextOutsideLeft,
  .doneCritText1.taskTextOutsideRight,
  .doneCritText2.taskTextOutsideLeft,
  .doneCritText2.taskTextOutsideRight,
  .doneCritText3.taskTextOutsideLeft,
  .doneCritText3.taskTextOutsideRight {
    fill: ${t.taskTextOutsideColor} !important;
  }

  .vert {
    stroke: ${t.vertLineColor};
  }

  .vertText {
    font-size: 15px;
    text-anchor: middle;
    fill: ${t.vertLineColor} !important;
  }

  .activeCritText0,
  .activeCritText1,
  .activeCritText2,
  .activeCritText3 {
    fill: ${t.taskTextDarkColor} !important;
  }

  .titleText {
    text-anchor: middle;
    font-size: 18px;
    fill: ${t.titleColor||t.textColor};
    font-family: ${t.fontFamily};
  }
`,"getStyles");t.s(["diagram",0,{parser:_,db:tN,renderer:{setConf:tj,draw:tU},styles:tZ}],83025)}]);