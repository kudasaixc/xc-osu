#include <realm/db.hpp>
#include <realm/history.hpp>
#include <realm/group.hpp>
#include <realm/transaction.hpp>
#include <cstdio>
#include <thread>
#include <sys/stat.h>
using namespace realm;
int run();
int main() { try { return run(); } catch (const std::exception& e) { printf("EXC: %s\n", e.what()); fflush(stdout); return 1; } }
int run() {
    mkdir("/xc", 0777);
    auto h1 = make_in_realm_history();
    auto db1 = DB::create(*h1, "/xc/c.realm");
    { auto wt = db1->start_write(); auto t = wt->add_table("class_T"); t->add_column(type_Int, "v"); wt->commit(); }
    std::thread th([] {
        auto h2 = make_in_realm_history();
        auto db2 = DB::create(*h2, "/xc/c.realm");   // second connection on another thread
        for (int i = 0; i < 200; i++) { auto wt = db2->start_write(); wt->get_table("class_T")->create_object().set("v", i); wt->commit(); }
        printf("thread done\n"); fflush(stdout);
    });
    for (int i = 0; i < 200; i++) { auto wt = db1->start_write(); wt->get_table("class_T")->create_object().set("v", i); wt->commit(); }
    th.join();
    auto rt = db1->start_read();
    printf("rows %zu (expect 400)\n", rt->get_table("class_T")->size());
    printf("closing\n"); fflush(stdout);
    rt.reset(); db1->close(); db1.reset();
    printf("closed\n"); fflush(stdout);
    auto h3 = make_in_realm_history();
    auto db3 = DB::create(*h3, "/xc/c.realm");
    printf("reopened rows %zu\n", db3->start_read()->get_table("class_T")->size());
    return 0;
}
