using Common.Database.Repositories.Generic;
using DataAccessLayer.Entities;
using Microsoft.EntityFrameworkCore;

namespace DataAccessLayer.Repositories;

public sealed class RoomRepository(DbSet<Room> dbSet) : GenericRepository<Room>(dbSet), IRoomRepository;
