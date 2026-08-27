/* SPDX-License-Identifier: GPL-3.0-or-later
 * Native Wayland window-or-region picker for SnapX. One process owns one
 * output-sized layer-shell overlay. A click selects a hovered window; pointer
 * motion beyond DRAG_THRESHOLD selects a free-form region. Coordinates stay
 * in compositor fractional-logical units; do not set a buffer scale here.
 */
#define _POSIX_C_SOURCE 200809L
#include <errno.h>
#include <fcntl.h>
#include <limits.h>
#include <poll.h>
#include <signal.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/mman.h>
#include <unistd.h>
#include <wayland-client.h>
#include "layer-shell-client.h"

#define MAX_OUTPUTS 16
#define MAX_WINDOWS 512
#define DRAG_THRESHOLD 5
typedef struct { int x,y,w,h; } Rect;
typedef struct { struct wl_output *output; char name[128]; } Output;
static struct wl_display *display; static struct wl_compositor *compositor;
static struct wl_shm *shm; static struct zwlr_layer_shell_v1 *layer_shell;
static struct zwlr_layer_surface_v1 *layer_surface; static struct wl_surface *surface;
static struct wl_seat *seat; static struct wl_pointer *pointer; static struct wl_keyboard *keyboard;
static struct wl_shm_pool *pool; static struct wl_buffer *buffer;
static Output outputs[MAX_OUTPUTS]; static int output_count; static struct wl_output *target_output;
static char wanted_output[128]; static Rect windows[MAX_WINDOWS]; static int window_count;
static int origin_x,origin_y,logical_w,logical_h,configured_w,configured_h;
static int pointer_x=-1,pointer_y=-1,press_x,press_y,pressed,dragging,hovered=-1;
static uint32_t *pixels; static size_t buffer_size;
static Rect rendered; static int rendered_valid;
static int buffer_busy; static int redraw_pending;
static volatile sig_atomic_t running=1;
static void stop_signal(int sig){(void)sig;running=0;}
static int pool_file(size_t size){char path[]="/tmp/snapx-picker-XXXXXX";int fd=mkstemp(path);if(fd<0)return -1;unlink(path);if(ftruncate(fd,(off_t)size)<0){close(fd);return -1;}return fd;}
static uint32_t premultiply(uint32_t c){uint32_t a=c>>24,r=(c>>16)&255,g=(c>>8)&255,b=c&255;return(a<<24)|((r*a/255)<<16)|((g*a/255)<<8)|(b*a/255);}
static void fill(uint32_t*p,int w,int h,Rect r,uint32_t c){int x0=r.x<0?0:r.x,y0=r.y<0?0:r.y,x1=r.x+r.w>w?w:r.x+r.w,y1=r.y+r.h>h?h:r.y+r.h;for(int y=y0;y<y1;y++)for(int x=x0;x<x1;x++)p[(size_t)y*w+x]=c;}
static void border(uint32_t*p,int w,int h,Rect r,int t,uint32_t c){fill(p,w,h,(Rect){r.x,r.y,r.w,t},c);fill(p,w,h,(Rect){r.x,r.y+r.h-t,r.w,t},c);fill(p,w,h,(Rect){r.x,r.y,t,r.h},c);fill(p,w,h,(Rect){r.x+r.w-t,r.y,t,r.h},c);}
/* Corner-anchored rectangle for in-place resizing of the persistent buffer:
 * the press point never moves, so dragging only ever repaints the leading
 * edge strips instead of shifting the whole shape. Visually identical to the
 * previous min/max construction. */
static Rect drag_rect(void){Rect r;int fw,fh;fw=pointer_x-press_x;fh=pointer_y-press_y;r.w=fw<0?-fw:fw;r.h=fh<0?-fh:fh;r.x=r.w==0||fw>=0?press_x:pointer_x;r.y=r.h==0||fh>=0?press_y:pointer_y;return r;}
static int window_at(int x,int y){int match=-1;for(int i=0;i<window_count;i++){Rect r=windows[i];if(x>=r.x&&x<r.x+r.w&&y>=r.y&&y<r.y+r.h)match=i;}return match;}
static void draw_highlight(uint32_t *p,int w,int h,Rect r){fill(p,w,h,r,premultiply(0x224c8dffu));border(p,w,h,r,2,0xff4c8dffu);}
static Rect current_rect(void){Rect hi={0,0,0,0};if(dragging)hi=drag_rect();else if(hovered>=0){Rect g=windows[hovered];hi=(Rect){g.x-origin_x,g.y-origin_y,g.w,g.h};}return hi;}
static Rect old_new; /* union scratch for damage computation */
static void repaint_region(Rect region){uint32_t dim=premultiply(0x66000000u);int x0=region.x<0?0:region.x,y0=region.y<0?0:region.y,x1=region.x+region.w>configured_w?configured_w:region.x+region.w,y1=region.y+region.h>configured_h?configured_h:region.y+region.h;for(int y=y0;y<y1;y++){uint32_t*row=pixels+(size_t)y*configured_w;for(int x=x0;x<x1;x++)row[x]=dim;}if(current_rect().w>0&&current_rect().h>0)draw_highlight(pixels,configured_w,configured_h,current_rect());}
static void repaint(uint32_t *p,int w,int h){uint32_t dim=premultiply(0x66000000u);for(size_t i=0;i<(size_t)w*h;i++)p[i]=dim;draw_highlight(p,w,h,current_rect());}
static void rect_union(Rect a,Rect b,Rect*u){int x0=a.x<b.x?a.x:b.x,y0=a.y<b.y?a.y:b.y,x1=(a.x+a.w)>(b.x+b.w)?(a.x+a.w):(b.x+b.w),y1=(a.y+a.h)>(b.y+b.h)?(a.y+a.h):(b.y+b.h);u->x=x0;u->y=y0;u->w=x1-x0;u->h=y1-y0;}
static void request_redraw(void);
static void buffer_release(void*d,struct wl_buffer*b){(void)d;(void)b;buffer_busy=0;if(redraw_pending){redraw_pending=0;request_redraw();}}
static const struct wl_buffer_listener buffer_listener={.release=buffer_release};

/* Persistent single-buffer storage: allocated once per configuration instead
 * of a fresh fd+mmap+pool on every pointer event. Re-created only if the
 * layer surface hands us a different size. */
static int buffer_w,buffer_h;
static int ensure_buffer(void){
	if(pixels&&buffer&&pool&&configured_w==buffer_w&&configured_h==buffer_h&&buffer_size==(size_t)configured_w*4*(size_t)configured_h)return 1;
	if(configured_w<=0||configured_h<=0)return 0;
	if(configured_w>INT_MAX/4||configured_h>INT_MAX)return 0;
	size_t stride=(size_t)configured_w*4,size=stride*(size_t)configured_h;
	if(size>(size_t)INT_MAX)return 0;
	int fd=pool_file(size);
	if(fd<0)return 0;
	uint32_t*p=mmap(NULL,size,PROT_READ|PROT_WRITE,MAP_SHARED,fd,0);
	if(p==MAP_FAILED){close(fd);return 0;}
	struct wl_shm_pool*np=wl_shm_create_pool(shm,fd,(int)size);
	if(!np){munmap(p,size);close(fd);return 0;}
	struct wl_buffer*nb=wl_shm_pool_create_buffer(np,0,configured_w,configured_h,(int)stride,WL_SHM_FORMAT_ARGB8888);
	close(fd);
	if(!nb){wl_shm_pool_destroy(np);munmap(p,size);return 0;}
	wl_buffer_add_listener(nb,&buffer_listener,NULL);
	if(buffer)wl_buffer_destroy(buffer);
	if(pool)wl_shm_pool_destroy(pool);
	pixels=p;buffer_size=size;buffer_w=configured_w;buffer_h=configured_h;buffer=nb;pool=np;rendered_valid=0;buffer_busy=0;redraw_pending=0;return 1;}

/* Repaints the committed highlight area plus the new one in place, then
 * damages exactly their union. All other pixels are untouched and undamaged,
 * so pointer motion no longer rewrites or uploads the whole output buffer. */
static void request_redraw(void){if(configured_w<=0||configured_h<=0)return;if(!ensure_buffer())return;Rect now=current_rect();if(rendered_valid&&now.x==rendered.x&&now.y==rendered.y&&now.w==rendered.w&&now.h==rendered.h)return;if(buffer_busy){redraw_pending=1;return;}if(!rendered_valid){repaint(pixels,configured_w,configured_h);rendered=now;rendered_valid=1;wl_surface_attach(surface,buffer,0,0);wl_surface_damage_buffer(surface,0,0,configured_w,configured_h);buffer_busy=1;wl_surface_commit(surface);return;}rect_union(rendered,now,&old_new);repaint_region(old_new);rendered=now;wl_surface_attach(surface,buffer,0,0);wl_surface_damage_buffer(surface,old_new.x,old_new.y,old_new.w,old_new.h);buffer_busy=1;wl_surface_commit(surface);}
static void configured(void*d,struct zwlr_layer_surface_v1*l,uint32_t serial,uint32_t w,uint32_t h){(void)d;zwlr_layer_surface_v1_ack_configure(l,serial);configured_w=w?(int)w:logical_w;configured_h=h?(int)h:logical_h;rendered_valid=0;request_redraw();}
static void layer_closed(void*d,struct zwlr_layer_surface_v1*l){(void)d;(void)l;running=0;}
static const struct zwlr_layer_surface_v1_listener layer_listener={.configure=configured,.closed=layer_closed};
static void log_hover(void){if(hovered>=0){Rect r=windows[hovered];fprintf(stderr,"phase=hover output=%s window=%d geometry=%d,%d %dx%d\n",wanted_output,hovered,r.x,r.y,r.w,r.h);}}
static void p_enter(void*d,struct wl_pointer*p,uint32_t s,struct wl_surface*sf,wl_fixed_t x,wl_fixed_t y){(void)d;(void)p;(void)s;(void)sf;pointer_x=(int)wl_fixed_to_double(x);pointer_y=(int)wl_fixed_to_double(y);hovered=window_at(origin_x+pointer_x,origin_y+pointer_y);log_hover();request_redraw();}
static void p_leave(void*d,struct wl_pointer*p,uint32_t s,struct wl_surface*sf){(void)d;(void)p;(void)s;(void)sf;if(!pressed){pointer_x=pointer_y=-1;hovered=-1;request_redraw();}}
static void p_motion(void*d,struct wl_pointer*p,uint32_t t,wl_fixed_t x,wl_fixed_t y){(void)d;(void)p;(void)t;pointer_x=(int)wl_fixed_to_double(x);pointer_y=(int)wl_fixed_to_double(y);int dx=pointer_x-press_x,dy=pointer_y-press_y;if(pressed&&!dragging&&(dx*dx+dy*dy>DRAG_THRESHOLD*DRAG_THRESHOLD)){dragging=1;fprintf(stderr,"phase=drag-start output=%s origin=%d,%d\n",wanted_output,origin_x+press_x,origin_y+press_y);rendered_valid=0;}int next=dragging?-1:window_at(origin_x+pointer_x,origin_y+pointer_y);if(next!=hovered){hovered=next;log_hover();}request_redraw();}
static void p_button(void*d,struct wl_pointer*p,uint32_t s,uint32_t t,uint32_t button_number,uint32_t state){(void)d;(void)p;(void)s;(void)t;if(button_number!=0x110)return;if(state==WL_POINTER_BUTTON_STATE_PRESSED){pressed=1;dragging=0;press_x=pointer_x;press_y=pointer_y;hovered=window_at(origin_x+pointer_x,origin_y+pointer_y);fprintf(stderr,"phase=press output=%s geometry=%d,%d\n",wanted_output,origin_x+press_x,origin_y+press_y);return;}if(!pressed)return;pressed=0;if(dragging){Rect r=drag_rect();if(r.w>0&&r.h>0){printf("region %d,%d %dx%d\n",origin_x+r.x,origin_y+r.y,r.w,r.h);fflush(stdout);running=0;}}else if(hovered>=0){Rect r=windows[hovered];printf("window %d,%d %dx%d\n",r.x,r.y,r.w,r.h);fflush(stdout);running=0;}}
static void p_axis(void*d,struct wl_pointer*p,uint32_t t,uint32_t a,wl_fixed_t v){(void)d;(void)p;(void)t;(void)a;(void)v;}static void p_frame(void*d,struct wl_pointer*p){(void)d;(void)p;}static void p_source(void*d,struct wl_pointer*p,uint32_t s){(void)d;(void)p;(void)s;}static void p_stop(void*d,struct wl_pointer*p,uint32_t t,uint32_t a){(void)d;(void)p;(void)t;(void)a;}static void p_discrete(void*d,struct wl_pointer*p,uint32_t a,int32_t v){(void)d;(void)p;(void)a;(void)v;}static void p_value120(void*d,struct wl_pointer*p,uint32_t a,int32_t v){(void)d;(void)p;(void)a;(void)v;}static void p_direction(void*d,struct wl_pointer*p,uint32_t a,uint32_t dir){(void)d;(void)p;(void)a;(void)dir;}static void p_warp(void*d,struct wl_pointer*p,wl_fixed_t x,wl_fixed_t y){(void)d;(void)p;(void)x;(void)y;}
static const struct wl_pointer_listener pointer_listener={.enter=p_enter,.leave=p_leave,.motion=p_motion,.button=p_button,.axis=p_axis,.frame=p_frame,.axis_source=p_source,.axis_stop=p_stop,.axis_discrete=p_discrete,.axis_value120=p_value120,.axis_relative_direction=p_direction
#ifdef WL_POINTER_WARP_SINCE_VERSION
/* The warp member exists only on newer libwayland headers. */
,.warp=p_warp
#endif
};
static void k_keymap(void*d,struct wl_keyboard*k,uint32_t f,int32_t fd,uint32_t s){(void)d;(void)k;(void)f;(void)s;close(fd);}static void k_enter(void*d,struct wl_keyboard*k,uint32_t s,struct wl_surface*sf,struct wl_array*keys){(void)d;(void)k;(void)s;(void)sf;(void)keys;}static void k_leave(void*d,struct wl_keyboard*k,uint32_t s,struct wl_surface*sf){(void)d;(void)k;(void)s;(void)sf;}static void k_key(void*d,struct wl_keyboard*k,uint32_t s,uint32_t t,uint32_t key,uint32_t state){(void)d;(void)k;(void)s;(void)t;if(key==1&&state==WL_KEYBOARD_KEY_STATE_PRESSED){fprintf(stderr,"phase=cancel reason=escape\n");running=0;}}static void k_mod(void*d,struct wl_keyboard*k,uint32_t s,uint32_t dep,uint32_t lat,uint32_t lock,uint32_t grp){(void)d;(void)k;(void)s;(void)dep;(void)lat;(void)lock;(void)grp;}static void k_repeat(void*d,struct wl_keyboard*k,int32_t rate,int32_t delay){(void)d;(void)k;(void)rate;(void)delay;}
static const struct wl_keyboard_listener keyboard_listener={.keymap=k_keymap,.enter=k_enter,.leave=k_leave,.key=k_key,.modifiers=k_mod,.repeat_info=k_repeat};
static void seat_caps(void*d,struct wl_seat*s,enum wl_seat_capability caps){(void)d;if((caps&WL_SEAT_CAPABILITY_POINTER)&&!pointer){pointer=wl_seat_get_pointer(s);wl_pointer_add_listener(pointer,&pointer_listener,NULL);}if((caps&WL_SEAT_CAPABILITY_KEYBOARD)&&!keyboard){keyboard=wl_seat_get_keyboard(s);wl_keyboard_add_listener(keyboard,&keyboard_listener,NULL);}}static void seat_name(void*d,struct wl_seat*s,const char*n){(void)d;(void)s;(void)n;}static const struct wl_seat_listener seat_listener={.capabilities=seat_caps,.name=seat_name};
static void o_geometry(void*d,struct wl_output*o,int32_t x,int32_t y,int32_t pw,int32_t ph,int32_t sub,const char*make,const char*model,int32_t transform){(void)d;(void)o;(void)x;(void)y;(void)pw;(void)ph;(void)sub;(void)make;(void)model;(void)transform;}static void o_mode(void*d,struct wl_output*o,uint32_t flags,int32_t w,int32_t h,int32_t refresh){(void)d;(void)o;(void)flags;(void)w;(void)h;(void)refresh;}static void o_done(void*d,struct wl_output*o){(void)d;(void)o;}static void o_scale(void*d,struct wl_output*o,int32_t factor){(void)d;(void)o;(void)factor;}static void o_name(void*d,struct wl_output*o,const char*n){(void)o;Output*out=d;strncpy(out->name,n,sizeof(out->name)-1);}static void o_desc(void*d,struct wl_output*o,const char*n){(void)d;(void)o;(void)n;}static const struct wl_output_listener output_listener={.geometry=o_geometry,.mode=o_mode,.done=o_done,.scale=o_scale,.name=o_name,.description=o_desc};
static void global(void*d,struct wl_registry*r,uint32_t name,const char*iface,uint32_t version){(void)d;if(!strcmp(iface,wl_compositor_interface.name))compositor=wl_registry_bind(r,name,&wl_compositor_interface,version<4?version:4);else if(!strcmp(iface,wl_shm_interface.name))shm=wl_registry_bind(r,name,&wl_shm_interface,1);else if(!strcmp(iface,zwlr_layer_shell_v1_interface.name))layer_shell=wl_registry_bind(r,name,&zwlr_layer_shell_v1_interface,version<4?version:4);else if(!strcmp(iface,wl_seat_interface.name)&&!seat){seat=wl_registry_bind(r,name,&wl_seat_interface,version<5?version:5);wl_seat_add_listener(seat,&seat_listener,NULL);}else if(!strcmp(iface,wl_output_interface.name)&&version>=4&&output_count<MAX_OUTPUTS){Output*out=&outputs[output_count++];memset(out,0,sizeof(*out));out->output=wl_registry_bind(r,name,&wl_output_interface,4);wl_output_add_listener(out->output,&output_listener,out);}}
static void global_remove(void*d,struct wl_registry*r,uint32_t name){(void)d;(void)r;(void)name;}static const struct wl_registry_listener registry_listener={.global=global,.global_remove=global_remove};
static int parse_rect(const char*t,Rect*r){return sscanf(t,"%d,%d %dx%d",&r->x,&r->y,&r->w,&r->h)==4&&r->w>0&&r->h>0;}
int main(int argc,char**argv){if(argc<8){fprintf(stderr,"usage: snapx-picker --output NAME --origin X Y --size W H [--window 'X,Y WxH']...\n");return 2;}for(int i=1;i<argc;i++){if(!strcmp(argv[i],"--output")&&i+1<argc)strncpy(wanted_output,argv[++i],sizeof(wanted_output)-1);else if(!strcmp(argv[i],"--origin")&&i+2<argc){origin_x=atoi(argv[++i]);origin_y=atoi(argv[++i]);}else if(!strcmp(argv[i],"--size")&&i+2<argc){logical_w=atoi(argv[++i]);logical_h=atoi(argv[++i]);}else if(!strcmp(argv[i],"--window")&&i+1<argc&&window_count<MAX_WINDOWS){if(parse_rect(argv[++i],&windows[window_count]))window_count++;}}if(!wanted_output[0]||logical_w<=0||logical_h<=0)return 2;signal(SIGINT,stop_signal);signal(SIGTERM,stop_signal);display=wl_display_connect(NULL);if(!display){fprintf(stderr,"no Wayland display\n");return 1;}struct wl_registry*registry=wl_display_get_registry(display);wl_registry_add_listener(registry,&registry_listener,NULL);wl_display_roundtrip(display);wl_display_roundtrip(display);for(int i=0;i<output_count;i++)if(!strcmp(outputs[i].name,wanted_output)){target_output=outputs[i].output;break;}if(!compositor||!shm||!layer_shell||!target_output){fprintf(stderr,"missing Wayland interface/output %s\n",wanted_output);return 3;}surface=wl_compositor_create_surface(compositor);layer_surface=zwlr_layer_shell_v1_get_layer_surface(layer_shell,surface,target_output,ZWLR_LAYER_SHELL_V1_LAYER_OVERLAY,"snapx-window-region-picker");zwlr_layer_surface_v1_add_listener(layer_surface,&layer_listener,NULL);zwlr_layer_surface_v1_set_anchor(layer_surface,ZWLR_LAYER_SURFACE_V1_ANCHOR_TOP|ZWLR_LAYER_SURFACE_V1_ANCHOR_RIGHT|ZWLR_LAYER_SURFACE_V1_ANCHOR_BOTTOM|ZWLR_LAYER_SURFACE_V1_ANCHOR_LEFT);zwlr_layer_surface_v1_set_size(layer_surface,0,0);zwlr_layer_surface_v1_set_exclusive_zone(layer_surface,-1);zwlr_layer_surface_v1_set_keyboard_interactivity(layer_surface,ZWLR_LAYER_SURFACE_V1_KEYBOARD_INTERACTIVITY_EXCLUSIVE);wl_surface_commit(surface);int display_fd=wl_display_get_fd(display);while(running){wl_display_dispatch_pending(display);wl_display_flush(display);struct pollfd fds[2]={{display_fd,POLLIN,0},{STDIN_FILENO,POLLIN|POLLHUP,0}};int result=poll(fds,2,250);if(result<0&&errno!=EINTR)break;if(fds[1].revents&(POLLIN|POLLHUP)){char input[16];ssize_t n=read(STDIN_FILENO,input,sizeof(input));if(n<=0){fprintf(stderr,"phase=cancel reason=stdin-eof\n");break;}}if((fds[0].revents&POLLIN)&&wl_display_dispatch(display)<0)break;}if(keyboard)wl_keyboard_destroy(keyboard);if(pointer)wl_pointer_destroy(pointer);if(buffer)wl_buffer_destroy(buffer);if(pool)wl_shm_pool_destroy(pool);if(layer_surface)zwlr_layer_surface_v1_destroy(layer_surface);if(surface)wl_surface_destroy(surface);wl_display_disconnect(display);return 0;}
