import re,sys
def load(p):
    s=open(p,encoding='utf-8').read()
    els=re.findall(r'<(\w+)\s([^>]*?)/?>',s)
    out=[]
    for tag,attrs in els:
        d=dict(re.findall(r'(\w+)="([^"]*)"',attrs))
        d['_tag']=tag; out.append(d)
    return out
def consumers(els,uid):
    res=[]
    for e in els:
        for part in e.get('inputs','').split(','):
            if ':' in part:
                term,net=part.rsplit(':',1)
                if net.split('.')[0]==uid: res.append(f"{e['_tag']}:{e.get('_name',e.get('_id',''))}.{term}<-{net.split('.',1)[1]}")
    return sorted(res)
def producer(els,ctrl):
    return ctrl.get('inputs','')
for p in sys.argv[1:]:
    els=load(p); print('==',p)
    for e in els:
        if e['_tag'] in('Control','Indicator'):
            if e['_tag']=='Control': print(' ',e['_name'],'parent',e['uid_parent'],'->',consumers(els,e['uid']))
            else:
                net=e['inputs'].split(':',1)[1]; src=[x for x in els if x.get('uid')==net.split('.')[0]]
                print(' ',e['_name'],'parent',e['uid_parent'],'<-',src[0]['_tag']+':'+src[0].get('_name',src[0].get('_id','')) if src else '?', net.split('.',1)[1])
